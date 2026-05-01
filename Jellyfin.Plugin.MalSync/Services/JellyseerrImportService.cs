using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MalSync.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MalSync.Services;

/// <summary>
/// Reads the authenticated user's MAL anime list and creates Jellyseerr requests
/// for seasons matching configured import profiles.
/// Title lookup is done via Jellyseerr's own search API, so no TVDB numeric ID is required.
/// </summary>
public sealed class JellyseerrImportService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly MalAuthService _auth;
    private readonly ILogger<JellyseerrImportService> _logger;

    public JellyseerrImportService(
        IHttpClientFactory httpFactory,
        MalAuthService auth,
        ILogger<JellyseerrImportService> logger)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // PUBLIC ENTRY POINT
    // ═══════════════════════════════════════════════════════════════════════

    public async Task<List<string>> RunImportAsync(
        string jellyfinUserId,
        bool dryRun,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        void Log(string msg) { log.Add(msg); _logger.LogInformation("{Msg}", msg); onLog?.Invoke(msg); }

        var cfg = MalSyncPlugin.Instance!.Configuration;

        // ── Resolve per-user URL / key (fall back to global) ──────────────
        var userCfg = _auth.GetOrCreateUserConfig(jellyfinUserId);
        var effectiveUrl    = (userCfg.JellyseerrUrl?.Trim().TrimEnd('/') is { Length: > 0 } u)
                              ? u : cfg.JellyseerrUrl.TrimEnd('/');
        var effectiveApiKey = (userCfg.JellyseerrApiKey?.Trim() is { Length: > 0 } k)
                              ? k : cfg.JellyseerrApiKey;

        if (string.IsNullOrWhiteSpace(effectiveUrl) || string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            Log("[ERROR] Jellyseerr URL or API key is not configured (neither globally nor per-user).");
            return log;
        }

        // ── Verify Jellyseerr is reachable before doing any MAL work ──────
        try
        {
            using var probe = CreateJellyseerrClient(effectiveUrl, effectiveApiKey);
            var probeResp = await probe.GetAsync("/api/v1/settings/about", cancellationToken)
                                       .ConfigureAwait(false);
            if (!probeResp.IsSuccessStatusCode)
            {
                Log($"[ERROR] Jellyseerr connectivity check failed: HTTP {(int)probeResp.StatusCode} from {effectiveUrl}/api/v1/settings/about — verify the URL and API key in plugin settings.");
                return log;
            }
        }
        catch (Exception ex)
        {
            Log($"[ERROR] Cannot reach Jellyseerr at {effectiveUrl}: {ex.Message} — verify the URL in plugin settings.");
            return log;
        }

        var profiles = cfg.JellyseerrProfiles;
        if (profiles.Count == 0)
        {
            Log("[ERROR] No import profiles configured. Add at least one profile in Admin → MAL Sync → Import tab.");
            return log;
        }

        // ── Build status → profile lookup ─────────────────────────────────
        var statusMap = new Dictionary<string, JellyseerrImportProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
            foreach (var s in p.Statuses)
                statusMap.TryAdd(s, p);

        var allStatuses = statusMap.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── Get MAL token ─────────────────────────────────────────────────
        var token = await _auth.GetAccessTokenAsync(jellyfinUserId).ConfigureAwait(false);
        if (token is null)
        {
            Log($"[ERROR] No valid MAL token for user {jellyfinUserId}. Please authenticate first.");
            return log;
        }

        // ── Fetch MAL list ────────────────────────────────────────────────
        Log("Fetching MAL anime list…");
        var malEntries = await FetchMalListAsync(token, cancellationToken).ConfigureAwait(false);
        Log($"MAL list loaded: {malEntries.Count} entries.");

        var toRequest = malEntries
            .Where(e => allStatuses.Contains(e.ListStatus?.Status ?? ""))
            .ToList();

        Log($"Entries matching profile statuses ({string.Join(", ", allStatuses)}): {toRequest.Count}");

        if (toRequest.Count == 0) { Log("Nothing to import."); return log; }

        if (dryRun)
            Log("[DRY RUN – no requests will be submitted to Jellyseerr]");

        // ── Resolve Jellyseerr user ID (needed for X-Api-User header → override rules) ──
        var jellyseerrUserId = await GetJellyseerrUserIdAsync(
            effectiveUrl, effectiveApiKey, jellyfinUserId, cancellationToken).ConfigureAwait(false);
        if (jellyseerrUserId is not null)
            _logger.LogInformation("Jellyseerr user ID resolved: {Id}", jellyseerrUserId);
        else
            _logger.LogWarning("Could not resolve Jellyseerr user ID for Jellyfin user {UserId} — override rules may not apply.", jellyfinUserId);

        // ── Fetch all series tracked by Sonarr (via Jellyseerr settings) ──
        Log("Fetching Sonarr tracked series…");
        var (sonarrTracked, sonarrServerId, animeProfileId, animeDirectory, animeTagIds) =
            await FetchSonarrMonitoredAsync(effectiveUrl, effectiveApiKey, cancellationToken).ConfigureAwait(false);
        if (sonarrTracked is not null)
            Log($"Sonarr is tracking {sonarrTracked.Count} series — existing seasons will be skipped.");
        else
            Log("Sonarr not reachable via Jellyseerr settings — falling back to Jellyseerr media check only.");
        if (animeProfileId.HasValue)
            _logger.LogInformation("Anime routing: serverId={ServerId} profileId={Profile} rootFolder={Folder}",
                sonarrServerId, animeProfileId, animeDirectory);

        // ── Fetch existing Jellyseerr media (requests + scanned items) ────
        Log("Fetching existing Jellyseerr media…");
        var existingRequests = await FetchExistingJellyseerrRequestsAsync(
            effectiveUrl, effectiveApiKey, cancellationToken).ConfigureAwait(false);
        Log($"Jellyseerr is tracking {existingRequests.Count / 2} media item(s).");

        // ── Pre-fetch all MAL details in parallel (rate-limited to 3 concurrent) ──
        Log($"Pre-fetching MAL details for {toRequest.Count} entries…");
        var malDetailSemaphore = new SemaphoreSlim(3, 3);
        var detailTasks = toRequest.Select(async entry =>
        {
            await malDetailSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var d = await FetchMalAnimeDetailAsync(
                    entry.Node.Id.ToString(), token, cancellationToken).ConfigureAwait(false);
                return (entry.Node.Id, Detail: d);
            }
            finally { malDetailSemaphore.Release(); }
        });
        var detailResults = await Task.WhenAll(detailTasks).ConfigureAwait(false);
        var detailMap = detailResults.ToDictionary(x => x.Id, x => x.Detail);
        Log("MAL details pre-fetched.");

        int submitted = 0, skipped = 0, failed = 0;
        // Cache TMDB→(seasons, tvdbId, isAnime, rawSeasons) lookups to avoid redundant HTTP calls and transient failures
        var tvDetailCache = new Dictionary<int, (List<int> Seasons, int? TvdbId, bool IsAnime, List<JellyseerrTvSeason> RawSeasons)>();

        foreach (var entry in toRequest)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var malId     = entry.Node.Id.ToString();
            var title     = entry.Node.Title ?? malId;
            var malStatus = entry.ListStatus?.Status ?? "unknown";
            var profile   = statusMap[malStatus];

            // ── Skip OVAs and specials — they are not TV series in Sonarr ──
            var malMediaType = entry.Node.MediaType?.ToLowerInvariant();
            if (malMediaType is "ova" or "special" or "music")
            {
                _logger.LogDebug("'{Title}' is MAL type '{Type}', skipping.", title, malMediaType);
                skipped++;
                continue;
            }

            // Use pre-fetched MAL detail
            detailMap.TryGetValue(entry.Node.Id, out var detail);

            // ── Search Jellyseerr by title ─────────────────────────────────
            var queries = BuildSearchQueries(title, entry.Node.AlternativeTitles, detail);
            var (searchResult, searchError) = await SearchJellyseerrAsync(
                queries, effectiveUrl, effectiveApiKey, cancellationToken).ConfigureAwait(false);

            if (searchResult is null)
            {
                if (searchError is not null)
                    Log($"[ERROR] Jellyseerr search error for '{title}': {searchError}");
                else
                    Log($"[SKIP] '{title}' (MAL {malId}): not found in Jellyseerr. Searched: {string.Join(" / ", queries.Take(4).Select(q => $"'{q}'"))}");
                skipped++;
                continue;
            }

            // ── Skip movies (Radarr is not managed by this plugin) ────────
            if (string.Equals(searchResult.MediaType, "movie", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("'{Title}' is a movie (TMDB {Id}), skipping — movies require Radarr.", title, searchResult.Id);
                skipped++;
                continue;
            }

            // ── Determine season number ────────────────────────────────────
            string requestKey;
            int    seasonNumber = 1;

            // ── Fetch TV detail early — needed for season-name matching and Sonarr dedup ──
            if (!tvDetailCache.TryGetValue(searchResult.Id, out var tvDetail))
            {
                tvDetail = await FetchTvDetailAsync(
                    effectiveUrl, effectiveApiKey, searchResult.Id, cancellationToken).ConfigureAwait(false);
                tvDetailCache[searchResult.Id] = tvDetail;
            }

            if (string.Equals(searchResult.MediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                // Priority 1: extract season from title (e.g. "2nd Season", "Season 3", roman numerals)
                if (!TryExtractSeasonFromTitle(title, out seasonNumber) &&
                    !TryExtractSeasonFromTitle(entry.Node.AlternativeTitles?.En ?? "", out seasonNumber))
                {
                    // Priority 2: match the MAL title/alt-titles against TMDB season names.
                    // This is far more reliable than the MAL prequel-chain walk for franchise
                    // series (e.g. Monogatari) where MAL and TMDB organise seasons differently.
                    var allTitles = BuildSearchQueries(title, entry.Node.AlternativeTitles, detail);
                    if (!TryMatchSeasonByName(allTitles, tvDetail.RawSeasons, out seasonNumber))
                    {
                        // Priority 3: fall back to MAL prequel chain walk
                        seasonNumber = await DetermineSeasonNumberAsync(
                            malId, token, cancellationToken, detail).ConfigureAwait(false);
                    }
                    else
                    {
                        _logger.LogInformation(
                            "Season {Season} for '{Title}' resolved via TMDB season name match.",
                            seasonNumber, title);
                    }
                }

                requestKey = profile.RequestAllSeasons
                    ? $"tv:{searchResult.Id}:all"
                    : $"tv:{searchResult.Id}:s{seasonNumber}";
            }
            else
            {
                requestKey = $"movie:{searchResult.Id}";
            }

            if (sonarrTracked is not null)
            {
                var tvdbId = tvDetail.TvdbId;

                if (tvdbId.HasValue && sonarrTracked.TryGetValue(tvdbId.Value, out var trackedSeasons))
                {
                    // Skip if: requesting all seasons and the show exists in Sonarr at all,
                    //          OR requesting a specific season that already exists in Sonarr
                    //          (regardless of whether that season is monitored or not —
                    //           submitting a duplicate request causes Sonarr to re-scan)
                    bool inSonarr = profile.RequestAllSeasons
                        ? trackedSeasons.Count > 0
                        : trackedSeasons.Contains(seasonNumber);

                    if (inSonarr)
                    {
                        Log($"[SKIP] '{title}' S{seasonNumber} already in Sonarr (TVDB {tvdbId.Value}).");
                        skipped++;
                        continue;
                    }
                }
                else if (!tvdbId.HasValue)
                {
                    _logger.LogDebug("No TVDB ID found for '{Title}' (TMDB {TmdbId}) — Sonarr dedup skipped for this entry.",
                        title, searchResult.Id);
                }
            }

            // When Sonarr is reachable and we can confirm the show is NOT in Sonarr,
            // bypass the (potentially stale) Jellyseerr-based dedup checks below.
            // Sonarr is the authoritative source — a show deleted from Sonarr may still
            // appear as status=3 (Available) in Jellyseerr's DB.
            //
            // Two sub-cases that bypass Jellyseerr cache:
            //   a) TVDB ID is known and NOT present in Sonarr → confirmed absent.
            //   b) TVDB ID is unknown → we cannot confirm the show IS in Sonarr either,
            //      so treat as potentially absent. Better to submit a harmless duplicate
            //      request than to silently miss a series deleted from Sonarr.
            bool confirmedAbsentFromSonarr =
                sonarrTracked is not null
                && (!tvDetail.TvdbId.HasValue || !sonarrTracked.ContainsKey(tvDetail.TvdbId.Value));

            // ── Check Jellyseerr mediaInfo (catches pending requests + downloaded items) ─
            // Skip this entire check when Sonarr has authoritatively told us the show is absent.
            if (!confirmedAbsentFromSonarr && searchResult.MediaInfo is not null)
            {
                bool alreadyTracked;
                if (string.Equals(searchResult.MediaType, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    alreadyTracked = searchResult.MediaInfo.Status >= 2;
                }
                else if (profile.RequestAllSeasons)
                {
                    alreadyTracked = searchResult.MediaInfo.Status >= 5;
                }
                else
                {
                    var s = searchResult.MediaInfo.Seasons
                        ?.FirstOrDefault(x => x.SeasonNumber == seasonNumber);
                    alreadyTracked = s is not null && s.Status >= 2;
                }

                if (alreadyTracked)
                {
                    Log($"[SKIP] '{title}' S{seasonNumber} already tracked in Jellyseerr (mediaInfo status {searchResult.MediaInfo.Status}).");
                    skipped++;
                    continue;
                }
            }

            // ── Belt-and-suspenders: Jellyseerr media page cache ──────────
            // Skip entirely when Sonarr has confirmed the show is absent — stale Jellyseerr
            // cache entries (requests or media rows) must not override Sonarr's ground truth.
            if (!confirmedAbsentFromSonarr && existingRequests.Contains(requestKey))
            {
                Log($"[SKIP] '{title}' S{seasonNumber} — request key '{requestKey}' already in Jellyseerr request cache.");
                skipped++;
                continue;
            }

            string seasonDesc = profile.RequestAllSeasons ? "all seasons" : $"S{seasonNumber}";

            Log($"[REQUEST] '{title}' (MAL {malId}) → TMDB {searchResult.Id} {seasonDesc} [{malStatus}] [{profile.Name}]");

            if (!dryRun)
            {
                // For RequestAllSeasons, pass the season list from the already-cached tv detail lookup
                List<int>? explicitSeasons = null;
                if (profile.RequestAllSeasons && tvDetail.Seasons.Count > 0)
                    explicitSeasons = tvDetail.Seasons;

                // Explicit anime routing is only used as a FALLBACK when we could not resolve a
                // Jellyseerr user ID (i.e. X-Api-User cannot be sent).
                // When X-Api-User IS set, Jellyseerr evaluates the user's override rules
                // server-side. Sending explicit serverId/profileId/rootFolder in the body
                // would override those rules — which caused incorrect paths and quality profiles
                // to be used instead of the configured override rules.
                int?    explicitServerId   = null;
                int?    explicitProfileId  = null;
                string? explicitRootFolder = null;
                List<int>? explicitTags    = null;
                if (jellyseerrUserId is null
                    && tvDetail.IsAnime
                    && animeProfileId.HasValue
                    && animeDirectory is not null)
                {
                    explicitServerId   = sonarrServerId;
                    explicitProfileId  = animeProfileId;
                    explicitRootFolder = animeDirectory;
                    explicitTags       = animeTagIds is { Count: > 0 } ? animeTagIds : null;
                    _logger.LogDebug(
                        "Explicit anime routing for '{Title}': no Jellyseerr user resolved, using fallback profile.",
                        title);
                }

                var ok = await SubmitJellyseerrRequestAsync(
                    effectiveUrl, effectiveApiKey,
                    searchResult.Id, searchResult.MediaType ?? "tv",
                    seasonNumber, explicitSeasons, jellyseerrUserId,
                    explicitServerId, explicitProfileId, explicitRootFolder, explicitTags,
                    title, cancellationToken).ConfigureAwait(false);

                if (ok) submitted++;
                else { Log($"[ERROR] Failed to submit Jellyseerr request for '{title}'."); failed++; }
            }
            else
            {
                submitted++;
            }
        }

        var verb = dryRun ? "would be submitted" : "submitted";
        Log($"Import complete. {submitted} request(s) {verb}, {skipped} skipped, {failed} failed.");
        return log;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // MAL HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<List<MalListEntry>> FetchMalListAsync(string token, CancellationToken ct)
    {
        var result  = new List<MalListEntry>();
        var url     = "https://api.myanimelist.net/v2/users/@me/animelist";
        // alternative_titles fetched here so we don't need a per-entry detail call just for English names
        var @params = "fields=list_status,num_episodes,alternative_titles,media_type&limit=1000&nsfw=true";

        while (!string.IsNullOrEmpty(url))
        {
            using var http = CreateMalClient(token);
            var resp = await http.GetAsync(
                $"{url}{(url.Contains('?') ? "&" : "?")}{@params}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) break;

            var page = await resp.Content.ReadFromJsonAsync<MalListPage>(cancellationToken: ct)
                           .ConfigureAwait(false);
            if (page is null) break;
            if (page.Data is not null) result.AddRange(page.Data);
            url     = page.Paging?.Next ?? string.Empty;
            @params = string.Empty;
        }

        return result;
    }

    private async Task<MalAnimeDetail?> FetchMalAnimeDetailAsync(
        string malId, string token, CancellationToken ct)
    {
        try
        {
            using var http = CreateMalClient(token);
            var resp = await http.GetAsync(
                $"https://api.myanimelist.net/v2/anime/{malId}" +
                "?fields=external_links,related_anime,alternative_titles", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<MalAnimeDetail>(cancellationToken: ct)
                       .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAL detail fetch failed for {MalId}", malId);
            return null;
        }
    }

    /// <summary>
    /// Builds search query candidates in priority order:
    /// 1. English alt title from the MAL list entry (batch-fetched — no extra API call)
    /// 2. Non-Japanese synonyms from MAL
    /// 3. English alt title from separately-fetched detail (if available)
    /// 4. TVDB slug words (e.g. "attack-on-titan" → "attack on titan")
    /// 5. Original MAL title
    /// 6. Original title with season suffix stripped ("Show 2nd Season" → "Show")
    /// 7. English alt title with season suffix stripped
    /// </summary>
    private static List<string> BuildSearchQueries(
        string title,
        MalAlternativeTitles? nodeAltTitles,
        MalAnimeDetail? detail)
    {
        var queries = new List<string>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? q)
        {
            q = q?.Trim();
            if (string.IsNullOrWhiteSpace(q)) return;
            if (seen.Add(q)) queries.Add(q);
        }

        // 1. English alt title from the list batch call (most reliable)
        Add(nodeAltTitles?.En);

        // 2. Non-Japanese synonyms from list batch (+ season-stripped versions)
        foreach (var syn in nodeAltTitles?.Synonyms ?? [])
        {
            if (string.IsNullOrWhiteSpace(syn) || ContainsJapanese(syn)) continue;
            Add(syn);
            Add(StripSeasonSuffix(syn)); // "Scissor Seven Season 5" → "Scissor Seven"
        }

        // 3. English alt title from separately-fetched detail
        Add(detail?.AlternativeTitles?.En);

        // 4. TVDB slug words (e.g. "attack-on-titan" → "attack on titan")
        var tvdbLink = detail?.ExternalLinks?.FirstOrDefault(l =>
            (l.Url ?? "").Contains("thetvdb.com", StringComparison.OrdinalIgnoreCase));
        if (tvdbLink?.Url is not null)
        {
            try
            {
                var last = new Uri(tvdbLink.Url).Segments.Last().Trim('/');
                if (!string.IsNullOrEmpty(last) && !int.TryParse(last, out _))
                    Add(last.Replace('-', ' '));
            }
            catch { /* ignore malformed URL */ }
        }

        // 5. Original MAL title
        Add(title);

        // 6. Original title with season suffix stripped
        // ("Black Clover 2nd Season" → "Black Clover", "Date A Live V" → "Date A Live")
        Add(StripSeasonSuffix(title));

        // 7. English alt title stripped
        var enAlt = nodeAltTitles?.En ?? detail?.AlternativeTitles?.En;
        if (enAlt is not null)
            Add(StripSeasonSuffix(enAlt));

        // 8. Base show name before ": " subtitle separator — resolves OVA/arc entries to parent series
        // e.g. "Scissor Seven: Fragments of Memory" → "Scissor Seven"
        //      "Tougen Anki: Nikko Kegon Falls Arc"  → "Tougen Anki"
        //      "Dusk Maiden of Amnesia: Ghost Girl"   → "Dusk Maiden of Amnesia"
        foreach (var q in queries.ToList())
        {
            var ci = q.IndexOf(": ", StringComparison.Ordinal);
            if (ci > 0) Add(q[..ci].Trim());
        }

        return queries;
    }

    /// <summary>
    /// Tries to extract a season number directly from the title, avoiding the MAL prequel-chain walk.
    /// Returns false for first/standalone seasons (no explicit number in title).
    /// </summary>
    private static bool TryExtractSeasonFromTitle(string title, out int season)
    {
        season = 1;
        if (string.IsNullOrWhiteSpace(title)) return false;

        // "3rd Season", "2nd Season", "4th Season" etc.
        var m = Regex.Match(title, @"\b(\d+)(?:st|nd|rd|th)\s+Season\b", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 2) { season = n; return true; }

        // "Season 2", "Season 3" etc.
        m = Regex.Match(title, @"\bSeason\s+(\d+)\b", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out n) && n >= 2) { season = n; return true; }

        // Roman numerals at end: "Date A Live V", "Youjo Senki II"
        m = Regex.Match(title, @"\b(II|III|IV|V|VI|VII|VIII)\s*$", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            season = m.Groups[1].Value.ToUpperInvariant() switch
            {
                "II"   => 2, "III" => 3, "IV"  => 4,
                "V"    => 5, "VI"  => 6, "VII" => 7, "VIII" => 8,
                _      => 1
            };
            if (season >= 2) return true;
        }

        // Trailing standalone digit: "Isekai wa Smartphone to Tomo ni. 2"
        m = Regex.Match(title, @"\s+(\d+)\s*$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out n) && n >= 2 && n <= 20) { season = n; return true; }

        return false;
    }

    /// <summary>Removes trailing season/part/sequel indicators used by MAL but not TMDB.</summary>
    private static string StripSeasonSuffix(string title)
    {
        var t = title.Trim();
        // "2nd Season", "3rd Season", "4th Season" etc.
        t = Regex.Replace(t, @"\s+\d+(?:st|nd|rd|th)\s+Season\b.*$", "", RegexOptions.IgnoreCase).Trim();
        // "Season 2", "Season 5" etc.
        t = Regex.Replace(t, @"\s+Season\s+\d+\b.*$",               "", RegexOptions.IgnoreCase).Trim();
        // Roman numerals as suffix: "Date A Live V", "Mushoku Tensei III"
        t = Regex.Replace(t, @"\s+(?:II|III|IV|V|VI|VII|VIII)\s*$", "", RegexOptions.IgnoreCase).Trim();
        // Trailing single digit: "Tensei shitara Ken deshita 2"
        t = Regex.Replace(t, @"\s+\d+\s*$",                          "").Trim();
        // Year suffix: "Ranma ½ (2024)"
        t = Regex.Replace(t, @"\s+\(20\d{2}\)\s*$",                 "").Trim();
        return t;
    }

    private static bool ContainsJapanese(string s) =>
        s.Any(c => (c >= '\u3040' && c <= '\u30FF') || // Hiragana / Katakana
                   (c >= '\u4E00' && c <= '\u9FFF'));   // CJK unified ideographs

    /// <summary>
    /// Tries to identify the correct TMDB season number by fuzzy-matching the MAL title
    /// (and its alt-titles) against TMDB season names.
    ///
    /// TMDB names seasons after the arc/entry title for franchise series (e.g. Monogatari),
    /// so "Nisemonogatari" will match "Season 2 – Nisemonogatari" even though the MAL
    /// prequel chain would incorrectly return 6.
    ///
    /// Returns false when no confident match is found; the caller should then fall back to
    /// the MAL prequel-chain walk.
    /// </summary>
    private bool TryMatchSeasonByName(
        IReadOnlyList<string> candidateTitles,
        IReadOnlyList<JellyseerrTvSeason> tmdbSeasons,
        out int seasonNumber)
    {
        seasonNumber = 1;
        if (tmdbSeasons.Count == 0) return false;

        // Only attempt name matching when TMDB actually has meaningful season names
        // (some shows have generic "Season 1", "Season 2" names which aren't useful).
        var namedSeasons = tmdbSeasons
            .Where(s => !string.IsNullOrWhiteSpace(s.Name)
                     && !Regex.IsMatch(s.Name, @"^Season\s+\d+$", RegexOptions.IgnoreCase))
            .ToList();
        if (namedSeasons.Count == 0) return false;

        foreach (var candidate in candidateTitles)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var norm = NormalizeForMatch(candidate);

            foreach (var s in namedSeasons)
            {
                var sName = NormalizeForMatch(s.Name!);
                // Full containment in either direction is a confident match.
                // e.g. norm="nisemonogatari" ⊆ sName="nisemonogatari" → match
                //      norm="berserk of gluttony" ⊆ sName="berserk of gluttony season 2" → match
                if (sName.Contains(norm, StringComparison.Ordinal)
                 || norm.Contains(sName, StringComparison.Ordinal))
                {
                    seasonNumber = s.SeasonNumber;
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>Normalises a title for fuzzy season-name matching: lowercase, collapse whitespace,
    /// strip common noise words and punctuation.</summary>
    private static string NormalizeForMatch(string s)
    {
        s = s.ToLowerInvariant();
        // Strip leading "season N" prefix that TMDB sometimes prepends
        s = Regex.Replace(s, @"^season\s+\d+\s*[-–:]\s*", "");
        // Remove punctuation except spaces
        s = Regex.Replace(s, @"[^\w\s]", " ");
        // Collapse whitespace
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }


    private async Task<int> DetermineSeasonNumberAsync(
        string malId, string token, CancellationToken ct,
        MalAnimeDetail? prefetchedDetail = null)
    {
        try
        {
            int season        = 1;
            var visited       = new HashSet<string> { malId };
            var current       = malId;
            var currentDetail = prefetchedDetail;

            for (int depth = 0; depth < 20; depth++)
            {
                if (currentDetail is null)
                {
                    using var http = CreateMalClient(token);
                    var resp = await http.GetAsync(
                        $"https://api.myanimelist.net/v2/anime/{current}?fields=related_anime",
                        ct).ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) break;
                    currentDetail = await resp.Content.ReadFromJsonAsync<MalAnimeDetail>(
                        cancellationToken: ct).ConfigureAwait(false);
                }

                var prequel = currentDetail?.RelatedAnime?
                    .FirstOrDefault(r => string.Equals(
                        r.RelationType, "prequel", StringComparison.OrdinalIgnoreCase));

                if (prequel?.Node?.Id is null) break;

                var prequelId = prequel.Node.Id.ToString();
                if (!visited.Add(prequelId)) break;

                season++;
                current       = prequelId;
                currentDetail = null; // will fetch on next iteration
            }

            return season;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Season determination failed for MAL ID {MalId}", malId);
            return 1;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // JELLYSEERR HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    private async Task<(JellyseerrSearchResult? Result, string? Error)> SearchJellyseerrAsync(
        IEnumerable<string> queries, string baseUrl, string apiKey, CancellationToken ct)
    {
        string? firstError = null;
        foreach (var query in queries)
        {
            try
            {
                using var http = CreateJellyseerrClient(baseUrl, apiKey);
                var resp = await http.GetAsync(
                    $"/api/v1/search?query={Uri.EscapeDataString(query)}",
                    ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var errMsg = $"HTTP {(int)resp.StatusCode} from Jellyseerr search (query: '{query}')";
                    firstError ??= errMsg;
                    _logger.LogWarning("{Error}", errMsg);
                    continue;
                }

                var page = await resp.Content.ReadFromJsonAsync<JellyseerrSearchPage>(
                    cancellationToken: ct).ConfigureAwait(false);

                _logger.LogDebug("Jellyseerr search '{Query}': {Count} result(s)",
                    query, page?.Results?.Count ?? 0);

                var match = page?.Results?.FirstOrDefault(r =>
                    string.Equals(r.MediaType, "tv",    StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(r.MediaType, "movie", StringComparison.OrdinalIgnoreCase));

                if (match is not null) return (match, null);
            }
            catch (Exception ex)
            {
                var errMsg = $"Exception searching Jellyseerr for '{query}': {ex.Message}";
                firstError ??= errMsg;
                _logger.LogWarning(ex, "{Error}", errMsg);
            }
        }
        return (null, firstError);
    }

    private async Task<HashSet<string>> FetchExistingJellyseerrRequestsAsync(
        string baseUrl, string apiKey, CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // ── 1) /api/v1/media — items already in Sonarr/Radarr (scanned + downloaded) ──
            // NOTE: this endpoint returns seasons=[] for TV items, so it only gives us
            // whole-show keys (used by RequestAllSeasons mode). Per-season keys come from step 2.
            int skip = 0, total = int.MaxValue;
            while (skip < total)
            {
                using var http = CreateJellyseerrClient(baseUrl, apiKey);
                var resp = await http.GetAsync(
                    $"/api/v1/media?take=100&skip={skip}&filter=all&sort=added",
                    ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var result = await resp.Content.ReadFromJsonAsync<JellyseerrMediaPage>(
                    cancellationToken: ct).ConfigureAwait(false);
                if (result?.Results is null || result.Results.Count == 0) break;

                total = result.PageInfo?.Results ?? result.Results.Count;

                foreach (var media in result.Results)
                {
                    // status 1 = Unknown (not yet in Sonarr) — don't skip these
                    if (media.Status < 2) continue;

                    var tmdbId = media.TmdbId;
                    if (tmdbId is null or 0) continue;

                    if (string.Equals(media.MediaType, "movie", StringComparison.OrdinalIgnoreCase))
                    {
                        keys.Add($"movie:{tmdbId}");
                    }
                    else
                    {
                        // Add a per-show key so RequestAllSeasons can dedup by show
                        keys.Add($"tv:{tmdbId}:all");
                        // Add per-season keys if the API happens to return them
                        foreach (var s in media.Seasons ?? [])
                            if (s.Status >= 2)
                                keys.Add($"tv:{tmdbId}:s{s.SeasonNumber}");
                    }
                }

                skip += result.Results.Count;
                if (result.Results.Count < 100) break;
            }

            // ── 2) /api/v1/request — pending requests with exact per-season data ──
            // The media API returns seasons=[] so per-season keys must come from the request API.
            // This ensures re-runs don't re-submit requests that are still pending in Jellyseerr.
            int reqSkip = 0, reqTotal = int.MaxValue;
            while (reqSkip < reqTotal)
            {
                using var http = CreateJellyseerrClient(baseUrl, apiKey);
                var resp = await http.GetAsync(
                    $"/api/v1/request?take=100&skip={reqSkip}&filter=all&sort=added",
                    ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var page = await resp.Content.ReadFromJsonAsync<JellyseerrRequestPage>(
                    cancellationToken: ct).ConfigureAwait(false);
                if (page?.Results is null || page.Results.Count == 0) break;

                reqTotal = page.PageInfo?.Results ?? page.Results.Count;

                foreach (var req in page.Results)
                {
                    var tmdbId = req.Media?.TmdbId;
                    if (tmdbId is null or 0) continue;

                    var mediaType = req.Media?.MediaType;
                    if (string.Equals(mediaType, "movie", StringComparison.OrdinalIgnoreCase))
                    {
                        keys.Add($"movie:{tmdbId}");
                    }
                    else
                    {
                        if (req.Seasons is { Count: > 0 })
                            foreach (var s in req.Seasons)
                                keys.Add($"tv:{tmdbId}:s{s.SeasonNumber}");
                        else
                            keys.Add($"tv:{tmdbId}:all");
                    }
                }

                reqSkip += page.Results.Count;
                if (page.Results.Count < 100) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch existing Jellyseerr media.");
        }
        return keys;
    }

    // Reusable options that omit null fields — critical for Jellyseerr:
    // sending "serverId": null is interpreted as "route to server null" and causes
    // Jellyseerr to silently accept the HTTP request but never create it internally.
    // Omitting null fields lets Jellyseerr use its own defaults (and evaluate override rules).
    private static readonly JsonSerializerOptions _jsonOmitNulls = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<bool> SubmitJellyseerrRequestAsync(
        string baseUrl, string apiKey,
        int tmdbId, string mediaType,
        int seasonNumber, List<int>? explicitSeasons,
        string? jellyseerrUserId,
        int? serverId, int? profileId, string? rootFolder, List<int>? tags,
        string title, CancellationToken ct)
    {
        try
        {
            using var http = CreateJellyseerrClient(baseUrl, apiKey);
            // X-Api-User tells Jellyseerr to evaluate override rules in the context of
            // this specific user, so per-genre/language root folder & quality profile
            // overrides are applied instead of the server defaults.
            if (jellyseerrUserId is not null)
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-User", jellyseerrUserId);

            var body = new JellyseerrRequestBody
            {
                MediaType  = mediaType,
                MediaId    = tmdbId,
                ServerId   = serverId,
                ProfileId  = profileId,
                RootFolder = rootFolder,
                Tags       = tags,
            };

            if (string.Equals(mediaType, "tv", StringComparison.OrdinalIgnoreCase))
            {
                // Use pre-resolved season list when available (avoids redundant HTTP call)
                body.Seasons = explicitSeasons ?? [seasonNumber];
            }

            // Use _jsonOmitNulls so that null routing fields (serverId, profileId, rootFolder, tags)
            // are NOT included in the JSON body at all. Jellyseerr distinguishes between a missing
            // field (→ use server default / evaluate override rules) and an explicit null value
            // (→ route to "server null" → silent internal failure while still returning HTTP 2xx).
            var resp = await http.PostAsJsonAsync("/api/v1/request", body, _jsonOmitNulls, ct)
                                 .ConfigureAwait(false);

            var responseBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Jellyseerr request created for '{Title}' (TMDB {Id}) — response: {Body}",
                    title, tmdbId, responseBody);
                return true;
            }

            _logger.LogWarning(
                "Jellyseerr request failed for '{Title}': HTTP {Status} – {Err}",
                title, (int)resp.StatusCode, responseBody);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception submitting Jellyseerr request for '{Title}'", title);
            return false;
        }
    }

    /// <summary>Returns all regular (non-special) season numbers for a TMDB TV show via Jellyseerr,
    /// plus the TVDB ID (needed for Sonarr lookup) and the raw season list (with names for title matching).</summary>
    private async Task<(List<int> Seasons, int? TvdbId, bool IsAnime, List<JellyseerrTvSeason> RawSeasons)> FetchTvDetailAsync(
        string baseUrl, string apiKey, int tmdbId, CancellationToken ct)
    {
        try
        {
            using var http = CreateJellyseerrClient(baseUrl, apiKey);
            var resp = await http.GetAsync($"/api/v1/tv/{tmdbId}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return ([], null, false, []);

            var show = await resp.Content.ReadFromJsonAsync<JellyseerrTvDetail>(
                           cancellationToken: ct).ConfigureAwait(false);
            var rawSeasons = (show?.Seasons ?? [])
                .Where(s => s.SeasonNumber > 0)
                .OrderBy(s => s.SeasonNumber)
                .ToList();
            var seasons = rawSeasons.Select(s => s.SeasonNumber).ToList();

            // Detect anime: Japanese original language + Animation genre (TMDB genre ID 16).
            // We do this ourselves because Jellyseerr's auto-detection can silently fall back
            // to the default (Shows) profile for some titles.
            var isAnime = string.Equals(show?.OriginalLanguage, "ja", StringComparison.OrdinalIgnoreCase)
                       && (show?.Genres?.Any(g => g.Id == 16) ?? false);

            return (seasons, show?.ExternalIds?.TvdbId, isAnime, rawSeasons);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch TV detail for TMDB {Id}.", tmdbId);
            return ([], null, false, []);
        }
    }

    /// <summary>
    /// Fetches all series monitored in Sonarr (via Jellyseerr's settings).
    /// Returns a map of tvdbId → set of all season numbers tracked in Sonarr, plus the Sonarr server ID.
    /// Returns (null, null) if Sonarr is not configured.
    /// </summary>
    /// <summary>
    /// Looks up the Jellyseerr internal user ID (integer) for the given Jellyfin user UUID.
    /// Jellyseerr stores the Jellyfin user ID without dashes; we normalise both sides before comparing.
    /// </summary>
    private async Task<string?> GetJellyseerrUserIdAsync(
        string baseUrl, string apiKey, string jellyfinUserId, CancellationToken ct)
    {
        try
        {
            using var http = CreateJellyseerrClient(baseUrl, apiKey);
            var resp = await http.GetAsync("/api/v1/user?take=1000", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = await resp.Content.ReadFromJsonAsync<JellyseerrUserList>(
                cancellationToken: ct).ConfigureAwait(false);
            if (doc?.Results is null) return null;

            // Jellyseerr stores the UUID without dashes; normalise both sides.
            var needle = jellyfinUserId.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
            foreach (var u in doc.Results)
            {
                if (u.JellyfinUserId is null) continue;
                var candidate = u.JellyfinUserId
                    .Replace("-", "", StringComparison.Ordinal)
                    .ToLowerInvariant();
                if (candidate == needle)
                    return u.Id.ToString();
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not look up Jellyseerr user ID.");
            return null;
        }
    }

    private async Task<(Dictionary<int, HashSet<int>>? Tracked, int? ServerId, int? AnimeProfileId, string? AnimeDirectory, List<int>? AnimeTagIds)> FetchSonarrMonitoredAsync(
        string jellyseerrBaseUrl, string jellyseerrApiKey, CancellationToken ct)
    {
        try
        {
            // Step 1: Get Sonarr instances from Jellyseerr settings
            using var jf = CreateJellyseerrClient(jellyseerrBaseUrl, jellyseerrApiKey);
            var settingsResp = await jf.GetAsync("/api/v1/settings/sonarr", ct).ConfigureAwait(false);
            if (!settingsResp.IsSuccessStatusCode) return (null, null, null, null, null);

            var instances = await settingsResp.Content
                .ReadFromJsonAsync<List<JellyseerrSonarrInstance>>(cancellationToken: ct)
                .ConfigureAwait(false);

            var sonarr = instances?.FirstOrDefault(i => i.IsDefault)
                      ?? instances?.FirstOrDefault();
            if (sonarr?.ApiKey is null || sonarr.Hostname is null) return (null, null, null, null, null);

            var scheme    = sonarr.UseSsl ? "https" : "http";
            var sonarrUrl = $"{scheme}://{sonarr.Hostname}:{sonarr.Port}";

            // Step 2: Fetch all series from Sonarr
            using var http = _httpFactory.CreateClient("MalSync");
            http.BaseAddress = new Uri(sonarrUrl);
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", sonarr.ApiKey);
            var seriesResp = await http.GetAsync("/api/v3/series", ct).ConfigureAwait(false);
            if (!seriesResp.IsSuccessStatusCode) return (null, null, null, null, null);

            var series = await seriesResp.Content
                .ReadFromJsonAsync<List<SonarrSeries>>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (series is null) return (null, null, null, null, null);

            // Step 3: Build tvdbId → all-seasons map (monitored OR unmonitored)
            // We track every season that exists in Sonarr, not just monitored ones,
            // because submitting a Jellyseerr request for a season Sonarr already knows about
            // (even if unmonitored) causes Sonarr to re-scan and fires duplicate notifications.
            var map = new Dictionary<int, HashSet<int>>();
            foreach (var s in series)
            {
                if (s.TvdbId == 0) continue;
                var allSeasons = new HashSet<int>(
                    (s.Seasons ?? []).Where(season => season.SeasonNumber > 0)
                                    .Select(season => season.SeasonNumber));
                map[s.TvdbId] = allSeasons; // always add — show IS in Sonarr even if 0 seasons listed
            }
            return (map, sonarr.Id, sonarr.ActiveAnimeProfileId, sonarr.ActiveAnimeDirectory,
                    sonarr.AnimeTags is { Count: > 0 } ? sonarr.AnimeTags : null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not fetch Sonarr series list — Sonarr dedup disabled.");
            return (null, null, null, null, null);
        }
    }

    // ─── HTTP client factories ─────────────────────────────────────────────

    private HttpClient CreateMalClient(string token)
    {
        var http = _httpFactory.CreateClient("MalSync");
        http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        return http;
    }

    private HttpClient CreateJellyseerrClient(string baseUrl, string apiKey)
    {
        var http = _httpFactory.CreateClient("MalSync");
        http.BaseAddress = new Uri(baseUrl.TrimEnd('/'));
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
        return http;
    }

    // ═══════════════════════════════════════════════════════════════════════
    // DTOs
    // ═══════════════════════════════════════════════════════════════════════

    // ── MAL ───────────────────────────────────────────────────────────────
    private sealed class MalListPage
    {
        [JsonPropertyName("data")]   public List<MalListEntry>? Data   { get; set; }
        [JsonPropertyName("paging")] public MalPaging?          Paging { get; set; }
    }

    private sealed class MalPaging
    {
        [JsonPropertyName("next")] public string? Next { get; set; }
    }

    private sealed class MalListEntry
    {
        [JsonPropertyName("node")]        public MalAnimeNode   Node       { get; set; } = new();
        [JsonPropertyName("list_status")] public MalListStatus? ListStatus { get; set; }
    }

    private sealed class MalAnimeNode
    {
        [JsonPropertyName("id")]                 public int                   Id                { get; set; }
        [JsonPropertyName("title")]              public string?               Title             { get; set; }
        [JsonPropertyName("num_episodes")]       public int                   NumEpisodes       { get; set; }
        [JsonPropertyName("alternative_titles")] public MalAlternativeTitles? AlternativeTitles { get; set; }
        /// <summary>MAL media type: tv, ova, movie, special, ona, music</summary>
        [JsonPropertyName("media_type")]         public string?               MediaType         { get; set; }
    }

    private sealed class MalListStatus
    {
        [JsonPropertyName("status")]               public string? Status              { get; set; }
        [JsonPropertyName("num_episodes_watched")] public int     NumEpisodesWatched  { get; set; }
    }

    private sealed class MalAnimeDetail
    {
        [JsonPropertyName("id")]                 public int                       Id                { get; set; }
        [JsonPropertyName("external_links")]     public List<MalExternalLink>?    ExternalLinks     { get; set; }
        [JsonPropertyName("related_anime")]      public List<MalRelatedAnime>?    RelatedAnime      { get; set; }
        [JsonPropertyName("alternative_titles")] public MalAlternativeTitles?     AlternativeTitles { get; set; }
    }

    private sealed class MalAlternativeTitles
    {
        [JsonPropertyName("en")]       public string?       En       { get; set; }
        [JsonPropertyName("synonyms")] public List<string>? Synonyms { get; set; }
    }

    private sealed class MalExternalLink
    {
        [JsonPropertyName("url")]  public string? Url  { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class MalRelatedAnime
    {
        [JsonPropertyName("node")]          public MalAnimeNode? Node         { get; set; }
        [JsonPropertyName("relation_type")] public string?       RelationType { get; set; }
    }

    // ── Jellyseerr search ──────────────────────────────────────────────────
    private sealed class JellyseerrSearchPage
    {
        [JsonPropertyName("results")] public List<JellyseerrSearchResult>? Results { get; set; }
    }

    private sealed class JellyseerrSearchResult
    {
        [JsonPropertyName("id")]        public int                    Id        { get; set; }
        [JsonPropertyName("mediaType")] public string?                MediaType { get; set; }
        [JsonPropertyName("name")]      public string?                Name      { get; set; }
        [JsonPropertyName("title")]     public string?                Title     { get; set; }
        [JsonPropertyName("mediaInfo")] public JellyseerrMediaInfo?   MediaInfo { get; set; }
    }

    /// <summary>
    /// Jellyseerr media status: 1=Unknown 2=Pending 3=Processing 4=PartiallyAvailable 5=Available
    /// Anything ≥ 2 means the item is already tracked by Sonarr/Radarr.
    /// </summary>
    private sealed class JellyseerrMediaInfo
    {
        [JsonPropertyName("status")]  public int                        Status  { get; set; }
        [JsonPropertyName("seasons")] public List<JellyseerrMediaSeason>? Seasons { get; set; }
    }

    private sealed class JellyseerrMediaSeason
    {
        [JsonPropertyName("seasonNumber")] public int SeasonNumber { get; set; }
        [JsonPropertyName("status")]       public int Status       { get; set; }
    }

    // ── Jellyseerr existing media (all tracked items incl. Sonarr scans) ──
    private sealed class JellyseerrMediaPage
    {
        [JsonPropertyName("pageInfo")] public JellyseerrPageInfo?       PageInfo { get; set; }
        [JsonPropertyName("results")]  public List<JellyseerrMediaItem>? Results  { get; set; }
    }

    private sealed class JellyseerrMediaItem
    {
        [JsonPropertyName("tmdbId")]    public int?                         TmdbId    { get; set; }
        [JsonPropertyName("mediaType")] public string?                      MediaType { get; set; }
        [JsonPropertyName("status")]    public int                          Status    { get; set; }
        [JsonPropertyName("seasons")]   public List<JellyseerrMediaSeason>? Seasons   { get; set; }
    }

    // ── Jellyseerr existing requests (legacy, kept as fallback) ───────────
    private sealed class JellyseerrRequestPage
    {
        [JsonPropertyName("pageInfo")] public JellyseerrPageInfo?      PageInfo { get; set; }
        [JsonPropertyName("results")]  public List<JellyseerrRequest>? Results  { get; set; }
    }

    private sealed class JellyseerrPageInfo
    {
        [JsonPropertyName("results")] public int Results { get; set; }
    }

    private sealed class JellyseerrRequest
    {
        [JsonPropertyName("media")]   public JellyseerrMedia?           Media   { get; set; }
        [JsonPropertyName("seasons")] public List<JellyseerrSeasonInfo>? Seasons { get; set; }
    }

    private sealed class JellyseerrMedia
    {
        [JsonPropertyName("mediaType")] public string? MediaType { get; set; }
        [JsonPropertyName("tmdbId")]    public int?    TmdbId    { get; set; }
    }

    private sealed class JellyseerrSeasonInfo
    {
        [JsonPropertyName("seasonNumber")] public int SeasonNumber { get; set; }
    }

    // ── Jellyseerr user list (for X-Api-User header lookup) ──────────────
    private sealed class JellyseerrUserList
    {
        [JsonPropertyName("results")] public List<JellyseerrUser>? Results { get; set; }
    }

    private sealed class JellyseerrUser
    {
        [JsonPropertyName("id")]             public int     Id             { get; set; }
        [JsonPropertyName("jellyfinUserId")] public string? JellyfinUserId { get; set; }
    }

    // ── Jellyseerr request body ────────────────────────────────────────────
    private sealed class JellyseerrRequestBody
    {
        [JsonPropertyName("mediaType")] public string     MediaType  { get; set; } = "tv";
        [JsonPropertyName("mediaId")]   public int        MediaId    { get; set; }
        [JsonPropertyName("seasons")]   public List<int>? Seasons    { get; set; }
        // Anime routing: set explicitly when IsAnime=true so Jellyseerr always uses
        // the correct Sonarr profile and root folder regardless of its own auto-detection.
        // For non-anime shows these remain null → Jellyseerr uses its defaults.
        [JsonPropertyName("serverId")]   public int?      ServerId   { get; set; }
        [JsonPropertyName("profileId")]  public int?      ProfileId  { get; set; }
        [JsonPropertyName("rootFolder")] public string?   RootFolder { get; set; }
        [JsonPropertyName("tags")]       public List<int>? Tags      { get; set; }
    }

    // ── Jellyseerr TV detail (for season list + tvdbId + anime detection) ──
    private sealed class JellyseerrTvDetail
    {
        [JsonPropertyName("seasons")]          public List<JellyseerrTvSeason>?  Seasons          { get; set; }
        [JsonPropertyName("externalIds")]      public JellyseerrExternalIds?     ExternalIds      { get; set; }
        [JsonPropertyName("originalLanguage")] public string?                    OriginalLanguage { get; set; }
        [JsonPropertyName("genres")]           public List<JellyseerrGenre>?     Genres           { get; set; }
    }

    private sealed class JellyseerrGenre
    {
        [JsonPropertyName("id")]   public int    Id   { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
    }

    private sealed class JellyseerrExternalIds
    {
        [JsonPropertyName("tvdbId")] public int? TvdbId { get; set; }
    }

    private sealed class JellyseerrTvSeason
    {
        [JsonPropertyName("seasonNumber")] public int     SeasonNumber { get; set; }
        [JsonPropertyName("name")]         public string? Name         { get; set; }
    }

    // ── Sonarr (fetched via Jellyseerr settings proxy) ─────────────────────
    private sealed class JellyseerrSonarrInstance
    {
        [JsonPropertyName("id")]                   public int          Id                   { get; set; }
        [JsonPropertyName("name")]                 public string?      Name                 { get; set; }
        [JsonPropertyName("hostname")]             public string?      Hostname             { get; set; }
        [JsonPropertyName("port")]                 public int          Port                 { get; set; }
        [JsonPropertyName("apiKey")]               public string?      ApiKey               { get; set; }
        [JsonPropertyName("useSsl")]               public bool         UseSsl               { get; set; }
        [JsonPropertyName("isDefault")]            public bool         IsDefault            { get; set; }
        [JsonPropertyName("activeAnimeProfileId")] public int?         ActiveAnimeProfileId { get; set; }
        [JsonPropertyName("activeAnimeDirectory")] public string?      ActiveAnimeDirectory { get; set; }
        [JsonPropertyName("animeTags")]            public List<int>?   AnimeTags            { get; set; }
    }

    private sealed class SonarrSeries
    {
        [JsonPropertyName("tvdbId")]  public int                   TvdbId  { get; set; }
        [JsonPropertyName("seasons")] public List<SonarrSeason>?  Seasons { get; set; }
    }

    private sealed class SonarrSeason
    {
        [JsonPropertyName("seasonNumber")] public int  SeasonNumber { get; set; }
        [JsonPropertyName("monitored")]    public bool Monitored    { get; set; }
    }
}

