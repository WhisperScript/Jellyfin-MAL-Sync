using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MalSync.Services;

/// <summary>
/// Core synchronisation logic – C# port of jf_mal_sync.py.
/// Reads Jellyfin watch progress and pushes episode counts / statuses to MAL.
/// </summary>
public sealed class MalSyncService
{
    // ── Unicode normalisation map (matches Python script) ─────────────────
    private static readonly (string From, string To)[] UnicodeMap =
    {
        ("×","x"),("÷","/"),("：",":"),("・"," "),("！","!"),("？","?"),
        ("（","("),("）",")"),("【","["),("】","]"),("　"," "),
    };

    private static readonly Regex SequelRe = new(
        @"\b(2nd|3rd|4th|5th|6th|7th|8th|\d+th|season\s*[2-9]|part\s*[2-9]|\bii\b|\biii\b|\biv\b)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Some franchises encode sequel numbering using Japanese words, e.g. "... Ni!".
    private static readonly Regex JapaneseSequelSuffixRe = new(
        // Note: omit "san" to avoid false positives on honorific suffixes (e.g. "Alya-san").
        @"\b(ni|yon|shi|go|roku|nana|hachi|kyuu)\s*!?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IHttpClientFactory _httpFactory;
    private readonly MalAuthService _auth;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<MalSyncService> _logger;

    // Runtime MAL-ID caches (populated once per sync run)
    private readonly Dictionary<string, CacheEntry> _malIdCache = new();
    private readonly Dictionary<string, SyncState> _syncState = new();

    public MalSyncService(
        IHttpClientFactory httpFactory,
        MalAuthService auth,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        ILogger<MalSyncService> logger)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
    }

    // ═════════════════════════════════════════════════════════════════════
    // PUBLIC ENTRY POINT
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs a full sync for one Jellyfin user.
    /// </summary>
    /// <param name="jellyfinUserId">Jellyfin user-id to sync.</param>
    /// <param name="dryRun">When true, log what would happen without writing to MAL.</param>
    /// <param name="cancellationToken">Cancellation support.</param>
    /// <returns>Human-readable log lines collected during the run.</returns>
    public async Task<List<string>> SyncUserAsync(
        string jellyfinUserId,
        bool dryRun,
        bool debug = false,
        Action<string>? onLog = null,
        CancellationToken cancellationToken = default)
    {
        var log = new List<string>();
        void Log(string msg) { log.Add(msg); _logger.LogInformation("{Msg}", msg); onLog?.Invoke(msg); }
        void Dbg(string msg) { _logger.LogDebug("{Msg}", msg); if (debug) { var line = "[DEBUG] " + msg; log.Add(line); onLog?.Invoke(line); } }

        var cfg = MalSyncPlugin.Instance!.Configuration;
        var cacheScope = jellyfinUserId;

        // ── Resolve per-user settings (fall back to global) ───────────
        var userCfg = _auth.GetOrCreateUserConfig(jellyfinUserId);
        var effectiveNoDowngrade = userCfg.NoDowngrade ?? cfg.MalNoDowngrade;
        var effectiveJfUpdateWatched = userCfg.JfUpdateWatched ?? cfg.JfUpdateWatched;

        // ── Get MAL access token ───────────────────────────────────────
        var token = await _auth.GetAccessTokenAsync(jellyfinUserId).ConfigureAwait(false);
        if (token is null)
        {
            Log($"[ERROR] No valid MAL token for user {jellyfinUserId}. Please authenticate first.");
            return log;
        }

        var malHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" };

        // ── Get Jellyfin user object ───────────────────────────────────
        var jfUser = _userManager.GetUserById(Guid.Parse(jellyfinUserId));
        if (jfUser is null)
        {
            Log($"[ERROR] Jellyfin user {jellyfinUserId} not found.");
            return log;
        }

        // ── Fetch Jellyfin items ───────────────────────────────────────
        Log("Fetching Jellyfin metadata…");
        var jfItems = GetJfItems(jfUser);
        if (jfItems.Count == 0)
        {
            Log("[ERROR] No items returned from Jellyfin.");
            return log;
        }
        Dbg($"Jellyfin returned {jfItems.Count} movies/series.");

        // ── Fetch MAL user list (paginated) ────────────────────────────
        Log("Fetching MAL user list…");
        var (malUserList, malTitleEntries) = await FetchMalUserListAsync(malHeaders, cancellationToken).ConfigureAwait(false);
        var malAccountLabel = !string.IsNullOrWhiteSpace(userCfg.MalUsername) ? userCfg.MalUsername : jellyfinUserId;
        Log($"[MAL] Account '{malAccountLabel}': {malUserList.Count} list entr{(malUserList.Count == 1 ? "y" : "ies")}");
        Dbg($"MAL user list loaded: {malUserList.Count} entries.");

        if (malUserList.Count == 0)
        {
            Log("[SKIP] MAL list is empty for this account — nothing to sync.");
            return log;
        }

        // ── Filter anime series ────────────────────────────────────────
        var animePaths = cfg.GetAnimePaths();
        var animeSeries = jfItems
            .Where(i => i.Type == "Series"
                     && animePaths.Any(p => (i.Path ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        Dbg($"Processing {animeSeries.Count} series from anime folders.");

        // Season 1 MAL-ID cache keyed by Jellyfin series-id
        var s1IdCache = new Dictionary<string, string>();

        Log(dryRun ? "[DRY RUN – no changes will be written to MAL]" : "Starting sync…");

        foreach (var series in animeSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seriesName = series.Name ?? "Unknown";
            var seriesId = series.Id;

            // Load seasons
            var seasons = GetSeasons(Guid.Parse(seriesId), jfUser);
            var realSeasons = seasons.Where(s => (s.IndexNumber ?? 0) >= 1).ToList();
            if (realSeasons.Count == 0) { Dbg($"No real seasons for '{seriesName}', skipping."); continue; }

            foreach (var season in realSeasons)
            {
                var seasonNum = season.IndexNumber ?? 1;
                var seasonId = season.Id;
                var normalizedSeriesName = NormalizeTitle(seriesName);

                // ── Resolve MAL ID ─────────────────────────────────────
                string? malId = season.ProviderIds?.GetValueOrDefault("MyAnimeList");
                if (malId is not null)
                    Dbg($"Using Jellyfin season provider MAL ID {malId} for '{seriesName}' S{seasonNum}.");

                if (malId is null)
                {
                    malId = GetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, cfg.CacheTtlDays);
                    if (malId is not null)
                        Dbg($"Using cached MAL ID {malId} for '{seriesName}' S{seasonNum}.");
                }
                if (malId is not null && seasonNum == 1) s1IdCache.TryAdd(seriesId, malId);

                if (malId is null)
                {
                    malId = FindIdInUserList(malTitleEntries, seriesName, seasonNum, cfg.MalSearchMinSimilarity);
                    if (malId is not null)
                    {
                        Dbg($"Using MAL user-list match ID {malId} for '{seriesName}' S{seasonNum}.");
                        if (seasonNum == 1) s1IdCache.TryAdd(seriesId, malId);
                        SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId);
                    }
                }

                if (malId is null)
                {
                    if (seasonNum == 1)
                    {
                        malId = series.ProviderIds?.GetValueOrDefault("MyAnimeList");
                        if (malId is null)
                        {
                            Dbg($"No MAL ID for '{seriesName}' S1, searching by title…");
                            malId = await SearchMalIdAsync(seriesName, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                        }
                        if (malId is not null)
                        {
                            s1IdCache.TryAdd(seriesId, malId);
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId);
                        }
                    }
                    else
                    {
                        s1IdCache.TryGetValue(seriesId, out var s1Id);
                        if (s1Id is null)
                        {
                            var baseTitle = StripSeasonSuffix(seriesName);
                            Dbg($"No S1 cache for '{seriesName}', searching S1 by title '{baseTitle}'…");
                            s1Id = await SearchMalIdAsync(baseTitle, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                        }
                        if (s1Id is not null)
                        {
                            Dbg($"Traversing sequel chain for '{seriesName}' S{seasonNum} from S1 ID {s1Id}…");
                            malId = await GetMalSequelFromChainAsync(s1Id, seasonNum, seriesName, malHeaders, cancellationToken).ConfigureAwait(false);
                        }
                        if (malId is null)
                        {
                            var suffix = seasonNum switch { 2 => "2nd Season", 3 => "3rd Season", 4 => "4th Season", 5 => "5th Season", _ => $"{seasonNum}th Season" };
                            Dbg($"Sequel chain failed, direct search for '{seriesName} {suffix}'…");
                            malId = await SearchMalIdAsync($"{seriesName} {suffix}", malHeaders, seasonNum, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                        }
                        if (malId is not null) 
                        {
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId);
                        }
                    }
                }

                // Guard against S1 resolving to sequel IDs (e.g. "... 2", "... Ni!").
                if (seasonNum == 1 && malId is not null)
                {
                    var looksLikeSequel = await IsLikelySequelCandidateAsync(
                        malId, malUserList, malHeaders, cancellationToken).ConfigureAwait(false);

                    if (looksLikeSequel)
                    {
                        Dbg($"Rejecting S1 candidate MAL ID {malId} for '{seriesName}' because the title looks like a sequel. Retrying without this ID.");

                        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { malId };
                        var remapped = FindIdInUserList(
                            malTitleEntries,
                            seriesName,
                            seasonNum,
                            cfg.MalSearchMinSimilarity,
                            excluded);

                        if (remapped is null)
                        {
                            Dbg($"No usable MAL list match left for '{seriesName}' S1, searching title with exclusion…");
                            remapped = await SearchMalIdAsync(
                                seriesName,
                                malHeaders,
                                1,
                                cfg.MalSearchMinSimilarity,
                                cancellationToken,
                                excluded).ConfigureAwait(false);
                        }

                        if (remapped is not null)
                        {
                            malId = remapped;
                            s1IdCache[seriesId] = malId;
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId);
                            Dbg($"S1 remap for '{seriesName}': using MAL ID {malId} after sequel rejection.");
                        }
                        else
                        {
                            Dbg($"Skipping '{seriesName}' S1: only sequel-like MAL candidates were found.");
                            malId = null;
                        }
                    }
                }

                if (malId is not null && seasonNum == 1) s1IdCache.TryAdd(seriesId, malId);

                if (malId is null)
                {
                    Dbg($"Skipping '{seriesName}' S{seasonNum}: MAL ID not found.");
                    continue;
                }

                // ── Get MAL entry info ─────────────────────────────────
                malUserList.TryGetValue(malId, out var malEntry);
                int malTotal = malEntry?.Total ?? 0;
                var airingStatus = malEntry?.AiringStatus ?? string.Empty;

                if (malEntry is null)
                {
                    var info = await GetMalAnimeInfoAsync(malId, malHeaders, cancellationToken).ConfigureAwait(false);
                    malTotal = info.NumEpisodes;
                    airingStatus = info.Status ?? string.Empty;
                }
                Dbg($"'{seriesName}' S{seasonNum} → MAL ID {malId}, eps: {(malTotal > 0 ? malTotal : "?")}, airing: {airingStatus}");

                // ── Load Jellyfin episodes ─────────────────────────────
                var episodes = GetEpisodes(Guid.Parse(seasonId), jfUser);
                if (episodes.Count == 0) continue;

                // Season offset for absolute-numbered shows
                var minIdx = episodes.Min(e => e.IndexNumber ?? 1);
                var seasonOffset = minIdx > 12 ? minIdx - 1 : 0;

                var label = (seasonNum > 1 || realSeasons.Count > 1) ? $"{seriesName} S{seasonNum}" : seriesName;

                // ── MAL → Jellyfin: mark episodes played ───────────────
                if (effectiveJfUpdateWatched && malEntry?.Watched > 0)
                {
                    MarkJfWatched(jfUser, episodes, malEntry.Watched, seasonOffset, label, dryRun);
                }

                // ── Calculate watched count ────────────────────────────
                var watchedEps = episodes.Where(e => e.UserData?.Played == true).ToList();
                if (watchedEps.Count == 0) { Dbg($"  → '{label}': no episodes watched yet."); continue; }

                var rawMax = watchedEps.Max(e => e.IndexNumber ?? 0);
                var watchedCount = rawMax - seasonOffset;
                if (malTotal > 0) watchedCount = Math.Min(watchedCount, malTotal);

                var status = airingStatus == "finished_airing" && malTotal > 0 && watchedCount >= malTotal
                             ? "completed" : "watching";

                // ── Change detection ───────────────────────────────────
                if (malEntry is not null)
                {
                    if (effectiveNoDowngrade)
                    {
                        var rank = new Dictionary<string, int>
                        { ["completed"] = 3, ["watching"] = 2, ["on_hold"] = 1, ["plan_to_watch"] = 0, ["dropped"] = 0 };
                        if (watchedCount < malEntry.Watched
                            || rank.GetValueOrDefault(status) < rank.GetValueOrDefault(malEntry.Status ?? ""))
                        {
                            Dbg($"  → '{label}': skipping – would downgrade MAL (local {watchedCount} {status} | MAL {malEntry.Watched} {malEntry.Status}).");
                            continue;
                        }
                    }
                    if (malEntry.Watched == watchedCount && malEntry.Status == status)
                    {
                        Dbg($"  → '{label}': already up to date ({watchedCount} eps, {status}).");
                        continue;
                    }
                }
                else
                {
                    var syncStateKey = $"{cacheScope}::{malId}";
                    if (_syncState.TryGetValue(syncStateKey, out var last)
                        && last.WatchedCount == watchedCount && last.Status == status)
                    {
                        Dbg($"  → '{label}': no change since last run, skipping.");
                        continue;
                    }
                }

                // ── Write to MAL (or dry-run) ──────────────────────────
                if (dryRun)
                {
                    if (malEntry is not null)
                        Log($"[DRY RUN] {label}: would set ep {watchedCount}/{(malTotal > 0 ? malTotal : "?")} ({status})" +
                            $" – MAL currently has {malEntry.Watched}/{(malTotal > 0 ? malTotal : "?")} ({malEntry.Status}) [ID {malId}]");
                    else
                        Log($"[DRY RUN] {label}: would set ep {watchedCount}/{(malTotal > 0 ? malTotal : "?")} ({status})" +
                            $" – not in MAL list yet [ID {malId}]");
                }
                else
                {
                    using var http = _httpFactory.CreateClient("MalSync");
                    foreach (var (k, v) in malHeaders) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

                    var resp = await http.PutAsync(
                        $"https://api.myanimelist.net/v2/anime/{malId}/my_list_status",
                        new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["num_watched_episodes"] = watchedCount.ToString(),
                            ["status"] = status,
                        }),
                        cancellationToken).ConfigureAwait(false);

                    if (resp.IsSuccessStatusCode)
                    {
                        Log($"[MAL] {label}: {watchedCount}/{(malTotal > 0 ? malTotal : "?")} eps ({status})");
                        _syncState[$"{cacheScope}::{malId}"] = new SyncState(watchedCount, status);
                    }
                    else
                    {
                        var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                        Log($"[MAL ERROR] Could not sync '{label}': {body}");
                    }
                }
            }
        }

        Log(dryRun ? "Dry-run complete." : "Sync complete.");
        return log;
    }

    // ═════════════════════════════════════════════════════════════════════
    // JELLYFIN HELPERS
    // ═════════════════════════════════════════════════════════════════════

    private List<JfItem> GetJfItems(User user)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Series, BaseItemKind.Movie],
            Recursive = true,
        });
        return items.Select(i => ToJfItem(i, user)).ToList();
    }

    private List<JfItem> GetSeasons(Guid seriesId, User user)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Season],
            ParentId = seriesId,
        });
        return items.Select(i => ToJfItem(i, user)).ToList();
    }

    private List<JfItem> GetEpisodes(Guid seasonId, User user)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            ParentId = seasonId,
            IsMissing = false,
        });
        return items.Select(i => ToJfItem(i, user)).ToList();
    }

    private JfItem ToJfItem(BaseItem item, User user)
    {
        var userData = _userDataManager.GetUserData(user, item);
        return new JfItem
        {
            Id = item.Id.ToString("N"),
            Name = item.Name,
            Type = item.GetType().Name,
            Path = item.Path,
            IndexNumber = item.IndexNumber,
            ProviderIds = item.ProviderIds?.ToDictionary(k => k.Key, v => v.Value),
            UserData = new JfUserData { Played = userData.Played },
        };
    }

    private void MarkJfWatched(
        User user, List<JfItem> episodes,
        int malWatched, int seasonOffset, string label, bool dryRun)
    {
        foreach (var ep in episodes)
        {
            var epIdx = (ep.IndexNumber ?? 0) - seasonOffset;
            if (epIdx <= 0) continue;
            if (epIdx <= malWatched && ep.UserData?.Played != true)
            {
                if (dryRun)
                {
                    _logger.LogInformation("[DRY RUN] {Label}: would mark ep {Idx} as watched in Jellyfin", label, epIdx);
                }
                else
                {
                    var item = _libraryManager.GetItemById(ep.Id);
                    if (item is not null)
                    {
                        var data = _userDataManager.GetUserData(user, item);
                        data.Played = true;
                        data.PlayCount = Math.Max(1, data.PlayCount);
                        data.LastPlayedDate = DateTime.UtcNow;
                        _userDataManager.SaveUserData(user, item, data,
                            UserDataSaveReason.TogglePlayed, CancellationToken.None);
                    }
                }
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // MAL API HELPERS
    // ═════════════════════════════════════════════════════════════════════

    private async Task<(Dictionary<string, MalUserEntry> List, List<(string Norm, string Id, string Title)> Titles)>
        FetchMalUserListAsync(Dictionary<string, string> headers, CancellationToken ct)
    {
        var list = new Dictionary<string, MalUserEntry>();
        var titles = new List<(string, string, string)>();
        var url = "https://api.myanimelist.net/v2/users/@me/animelist";
        var @params = "fields=list_status,num_episodes,alternative_titles,status&limit=1000&nsfw=true";

        while (!string.IsNullOrEmpty(url))
        {
            using var http = _httpFactory.CreateClient("MalSync");
            foreach (var (k, v) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

            var resp = await http.GetAsync($"{url}{(url.Contains('?') ? "&" : "?")}{@params}", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) break;

            var doc = await resp.Content.ReadFromJsonAsync<MalListPage>(cancellationToken: ct).ConfigureAwait(false);
            if (doc is null) break;

            foreach (var entry in doc.Data ?? Enumerable.Empty<MalListEntry>())
            {
                var node = entry.Node;
                var mid = node.Id.ToString();
                var lst = entry.ListStatus ?? new();
                var alt = node.AlternativeTitles ?? new();

                var ue = new MalUserEntry
                {
                    Title = node.Title ?? "",
                    Total = node.NumEpisodes,
                    AiringStatus = node.Status ?? "",
                    Watched = lst.NumEpisodesWatched,
                    Status = lst.Status ?? "",
                };
                list[mid] = ue;

                var tList = new List<string> { ue.Title };
                if (!string.IsNullOrEmpty(alt.En)) tList.Add(alt.En);
                if (alt.Synonyms is not null) tList.AddRange(alt.Synonyms);
                foreach (var t in tList.Where(t => !string.IsNullOrEmpty(t)))
                    titles.Add((NormalizeTitle(t), mid, ue.Title));
            }

            url = doc.Paging?.Next ?? string.Empty;
            @params = string.Empty; // next URL already has all query params
        }

        return (list, titles);
    }

    private async Task<MalAnimeInfo> GetMalAnimeInfoAsync(
        string malId, Dictionary<string, string> headers, CancellationToken ct)
    {
        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            foreach (var (k, v) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
            var resp = await http.GetAsync(
                $"https://api.myanimelist.net/v2/anime/{malId}?fields=num_episodes,status", ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
                return await resp.Content.ReadFromJsonAsync<MalAnimeInfo>(cancellationToken: ct).ConfigureAwait(false)
                       ?? new();
        }
        catch (Exception ex) { _logger.LogDebug(ex, "MAL anime info fetch failed for ID {Id}", malId); }
        return new();
    }

    private async Task<string?> GetMalSequelFromChainAsync(
        string baseId, int targetSeason, string seriesName,
        Dictionary<string, string> headers, CancellationToken ct,
        int maxHops = 12)
    {
        var chain = new List<(string Id, string Title)>();
        var current = baseId;
        var visited = new HashSet<string> { baseId };

        for (var hop = 0; hop < maxHops; hop++)
        {
            try
            {
                using var http = _httpFactory.CreateClient("MalSync");
                foreach (var (k, v) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
                var resp = await http.GetAsync(
                    $"https://api.myanimelist.net/v2/anime/{current}?fields=related_anime,title", ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var doc = await resp.Content.ReadFromJsonAsync<MalRelatedResponse>(cancellationToken: ct).ConfigureAwait(false);
                var sequels = doc?.RelatedAnime?
                    .Where(r => r.RelationType is "sequel" or "alternative_version"
                             && !visited.Contains(r.Node.Id.ToString()))
                    .ToList() ?? new();

                if (sequels.Count == 0) break;

                var node = sequels[0].Node;
                var nid = node.Id.ToString();
                visited.Add(nid);
                chain.Add((nid, node.Title ?? ""));
                current = nid;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Sequel chain error at ID {Id}", current); break; }
        }

        if (chain.Count == 0) return null;

        var baseTitle = StripSeasonSuffix(seriesName);

        // 1. Season-number match
        foreach (var (cid, ctitle) in chain)
            if (ContainsSeasonNumber(ctitle, targetSeason)
                && TitleSimilarity(baseTitle, StripSeasonSuffix(ctitle)) >= 0.4)
                return cid;

        // 2. Index fallback
        var pos = targetSeason - 2;
        if (pos >= 0 && pos < chain.Count) return chain[pos].Id;

        // 3. Last entry
        return chain[^1].Id;
    }

    private async Task<string?> SearchMalIdAsync(
        string title, Dictionary<string, string> headers, int seasonNum,
        double minSimilarity, CancellationToken ct,
        ISet<string>? excludedIds = null)
    {
        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            foreach (var (k, v) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
            var resp = await http.GetAsync(
                $"https://api.myanimelist.net/v2/anime?q={Uri.EscapeDataString(title)}&limit=5&fields=id,title,alternative_titles",
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = await resp.Content.ReadFromJsonAsync<MalSearchPage>(cancellationToken: ct).ConfigureAwait(false);
            string? bestId = null;
            double bestScore = 0;
            string? bestNonSequelId = null;
            double bestNonSequelScore = 0;

            var baseQuery = StripSeasonSuffix(title);

            foreach (var entry in doc?.Data ?? Enumerable.Empty<MalSearchEntry>())
            {
                var node = entry.Node;
                var nodeId = node.Id.ToString();
                if (excludedIds is not null && excludedIds.Contains(nodeId))
                    continue;

                var alt = node.AlternativeTitles ?? new();
                var candidates = new List<string> { node.Title ?? "" };
                if (!string.IsNullOrEmpty(alt.En)) candidates.Add(alt.En);
                if (alt.Synonyms is not null) candidates.AddRange(alt.Synonyms);

                var score = candidates.Max(c => TitleSimilarity(title, c));
                var allTitles = string.Join(" ", candidates);
                var isSequelCandidate = IsSequelTitle(allTitles);

                if (seasonNum == 1)
                {
                    var baseCandidates = candidates.Select(StripSeasonSuffix).ToList();
                    var baseScore = baseCandidates.Max(c => TitleSimilarity(baseQuery, c));
                    score = Math.Min(score, baseScore);

                    var qFirst = NormalizeTitle(baseQuery).Split(' ').FirstOrDefault() ?? string.Empty;
                    if (!string.IsNullOrEmpty(qFirst))
                    {
                        var firstScore = baseCandidates
                            .Select(c => NormalizeTitle(c).Split(' ').FirstOrDefault() ?? string.Empty)
                            .Select(w => Similarity(qFirst, w))
                            .DefaultIfEmpty(0).Max();
                        if (firstScore < 0.5) score *= 0.15;
                    }

                    // Strongly discourage sequel-looking titles when searching for S1.
                    if (isSequelCandidate) score *= 0.12;
                }
                else
                {
                    var baseQ = StripSeasonSuffix(title);
                    var bases = candidates.Select(StripSeasonSuffix).ToList();
                    var bScore = bases.Max(c => TitleSimilarity(baseQ, c));
                    if (!ContainsSeasonNumber(allTitles, seasonNum)) bScore *= 0.4;

                    if (bScore > 0 && baseQ.Split(' ').Length > 0)
                    {
                        var qFirst = baseQ.Split(' ')[0].ToLowerInvariant();
                        var maxFirst = candidates
                            .Select(c => StripSeasonSuffix(c).Split(' ').FirstOrDefault()?.ToLowerInvariant() ?? "")
                            .Select(w => TitleSimilarity(qFirst, w))
                            .DefaultIfEmpty(0).Max();
                        if (maxFirst < 0.5) bScore *= 0.15;
                    }
                    score = Math.Min(score, bScore);
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestId = nodeId;
                }

                if (seasonNum == 1 && !isSequelCandidate && score > bestNonSequelScore)
                {
                    bestNonSequelScore = score;
                    bestNonSequelId = nodeId;
                }
            }

            // For S1, prefer non-sequel matches only; this avoids mapping S1 → S2
            // when both entries have very similar base titles.
            if (seasonNum == 1)
                return bestNonSequelScore >= minSimilarity ? bestNonSequelId : null;

            if (bestScore >= minSimilarity) return bestId;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "MAL search failed for '{Title}'", title); }
        return null;
    }

    private string? FindIdInUserList(
        List<(string Norm, string Id, string Title)> entries,
        string seriesName, int seasonNum, double minSimilarity,
        ISet<string>? excludedIds = null)
    {
        if (entries.Count == 0) return null;

        string? bestId = null;
        double bestScore = 0;
        string? bestNonSequelId = null;
        double bestNonSequelScore = 0;

        if (seasonNum == 1)
        {
            var normQ = NormalizeTitle(seriesName);
            var baseQ = NormalizeTitle(StripSeasonSuffix(seriesName));
            var qFirst = baseQ.Split(' ').FirstOrDefault() ?? string.Empty;
            foreach (var (norm, mid, _) in entries)
            {
                if (excludedIds is not null && excludedIds.Contains(mid))
                    continue;

                var isSequelCandidate = IsSequelTitle(norm);
                var score = Similarity(normQ, norm);
                var baseT = NormalizeTitle(StripSeasonSuffix(norm));
                score = Math.Min(score, Similarity(baseQ, baseT));

                if (!string.IsNullOrEmpty(qFirst))
                {
                    var tFirst = baseT.Split(' ').FirstOrDefault() ?? string.Empty;
                    if (Similarity(qFirst, tFirst) < 0.5) score *= 0.15;
                }

                // Strongly discourage sequel-looking titles when matching S1.
                if (isSequelCandidate) score *= 0.12;
                if (score > bestScore) { bestScore = score; bestId = mid; }

                if (!isSequelCandidate && score > bestNonSequelScore)
                {
                    bestNonSequelScore = score;
                    bestNonSequelId = mid;
                }
            }

            // For S1, only accept non-sequel candidates from the user's list.
            return bestNonSequelScore >= minSimilarity ? bestNonSequelId : null;
        }
        else
        {
            var baseQ = NormalizeTitle(StripSeasonSuffix(seriesName));
            foreach (var (norm, mid, orig) in entries)
            {
                if (excludedIds is not null && excludedIds.Contains(mid))
                    continue;

                var baseT = NormalizeTitle(StripSeasonSuffix(orig));
                var score = Similarity(baseQ, baseT);
                if (!ContainsSeasonNumber(orig, seasonNum)) score *= 0.4;

                var qParts = baseQ.Split(' ');
                var tParts = baseT.Split(' ');
                if (qParts.Length > 0 && tParts.Length > 0
                    && Similarity(qParts[0], tParts[0]) < 0.5)
                    score *= 0.15;

                if (score > bestScore) { bestScore = score; bestId = mid; }
            }
        }

        return bestScore >= minSimilarity ? bestId : null;
    }

    private async Task<bool> IsLikelySequelCandidateAsync(
        string malId,
        Dictionary<string, MalUserEntry> malUserList,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        // Prefer title from the authenticated user's list (no extra call).
        if (malUserList.TryGetValue(malId, out var listEntry)
            && !string.IsNullOrWhiteSpace(listEntry.Title)
            && IsSequelTitle(listEntry.Title))
            return true;

        // Fallback: fetch title/alt titles directly for this MAL ID.
        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            foreach (var (k, v) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

            var resp = await http.GetAsync(
                $"https://api.myanimelist.net/v2/anime/{malId}?fields=title,alternative_titles",
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return false;

            var info = await resp.Content.ReadFromJsonAsync<MalNode>(cancellationToken: ct).ConfigureAwait(false);
            if (info is null) return false;

            var titles = new List<string>();
            if (!string.IsNullOrWhiteSpace(info.Title)) titles.Add(info.Title);
            if (!string.IsNullOrWhiteSpace(info.AlternativeTitles?.En)) titles.Add(info.AlternativeTitles.En!);
            if (info.AlternativeTitles?.Synonyms is not null)
                titles.AddRange(info.AlternativeTitles.Synonyms.Where(s => !string.IsNullOrWhiteSpace(s))!);

            return titles.Any(IsSequelTitle);
        }
        catch
        {
            return false;
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // MAL-ID CACHE (in-memory for session; can be persisted via plugin storage)
    // ═════════════════════════════════════════════════════════════════════

    private string? GetCachedMalId(string userScope, string series, int season, int ttlDays)
    {
        var key = $"{userScope}::{series}::{season}";
        if (!_malIdCache.TryGetValue(key, out var entry)) return null;
        if ((DateTime.UtcNow - entry.CachedAt).TotalDays > ttlDays) { _malIdCache.Remove(key); return null; }
        return entry.MalId;
    }

    private void SetCachedMalId(string userScope, string series, int season, string malId)
        => _malIdCache[$"{userScope}::{series}::{season}"] = new CacheEntry(malId, DateTime.UtcNow);

    // ═════════════════════════════════════════════════════════════════════
    // STRING / TITLE HELPERS  (mirrors the Python script)
    // ═════════════════════════════════════════════════════════════════════

    private static string NormalizeTitle(string t)
    {
        foreach (var (from, to) in UnicodeMap) t = t.Replace(from, to);
        return Regex.Replace(t.ToLowerInvariant().Trim(), @"\s+", " ");
    }

    private static double TitleSimilarity(string a, string b)
        => Similarity(NormalizeTitle(a), NormalizeTitle(b));

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
        // Levenshtein-based ratio (equivalent to Python difflib SequenceMatcher)
        int[,] dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                dp[i, j] = a[i - 1] == b[j - 1]
                    ? dp[i - 1, j - 1]
                    : 1 + Math.Min(dp[i - 1, j - 1], Math.Min(dp[i - 1, j], dp[i, j - 1]));

        var maxLen = Math.Max(a.Length, b.Length);
        return 1.0 - (double)dp[a.Length, b.Length] / maxLen;
    }

    private static string StripSeasonSuffix(string title)
    {
        title = title.Trim();
        string[] pats =
        {
            @"\s+\d+(?:st|nd|rd|th)\s+season\s*$",
            @"\s+season\s+\d+\s*$",
            @"\s+part\s+\d+\s*$",
            @"\s+[IVX]{1,4}\s*$",
            @"\s+\d+\s*$",
        };
        foreach (var p in pats)
            title = Regex.Replace(title, p, "", RegexOptions.IgnoreCase).Trim();
        return title;
    }

    private static bool IsSequelTitle(string text)
    {
        if (SequelRe.IsMatch(text)) return true;

        var t = NormalizeTitle(text);
        if (JapaneseSequelSuffixRe.IsMatch(t)) return true;

        // Trailing standalone number (e.g. "... 2") can indicate a sequel.
        if (Regex.IsMatch(t, @"\s+[2-9]\s*$", RegexOptions.IgnoreCase)) return true;

        return false;
    }

    private static bool ContainsSeasonNumber(string text, int n)
    {
        text = NormalizeTitle(text);

        // Numeric indicators: "2nd", "season 2", "part 2", trailing " 2".
        var ordinalSuffix = n switch
        {
            1 => "st",
            2 => "nd",
            3 => "rd",
            _ => "th",
        };

        if (Regex.IsMatch(text, $@"\b{n}{ordinalSuffix}\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, $@"\bseason\s*{n}\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, $@"\bpart\s*{n}\b", RegexOptions.IgnoreCase)) return true;
        if (Regex.IsMatch(text, $@"\b{n}\b", RegexOptions.IgnoreCase)) return true;

        // Roman numerals and Japanese words for common sequels.
        if (n == 2 && Regex.IsMatch(text, @"\bii\b|\bni\s*!?\s*$", RegexOptions.IgnoreCase)) return true;
        if (n == 3 && Regex.IsMatch(text, @"\biii\b", RegexOptions.IgnoreCase)) return true;
        if (n == 4 && Regex.IsMatch(text, @"\biv\b|\byon\s*!?\s*$|\bshi\s*!?\s*$", RegexOptions.IgnoreCase)) return true;
        if (n == 5 && Regex.IsMatch(text, @"\bv\b|\bgo\s*!?\s*$", RegexOptions.IgnoreCase)) return true;

        return false;
    }

    // ═════════════════════════════════════════════════════════════════════
    // LOCAL RECORD TYPES (JSON DTOs)
    // ═════════════════════════════════════════════════════════════════════

    private record CacheEntry(string MalId, DateTime CachedAt);
    private record SyncState(int WatchedCount, string Status);

    private sealed class MalUserEntry
    {
        public string Title { get; set; } = string.Empty;
        public int Total { get; set; }
        public string AiringStatus { get; set; } = string.Empty;
        public int Watched { get; set; }
        public string Status { get; set; } = string.Empty;
    }

    // ── Jellyfin JSON DTOs ─────────────────────────────────────────────
    private sealed class JfItemsResponse { [JsonPropertyName("Items")] public List<JfItem>? Items { get; set; } }
    private sealed class JfItem
    {
        [JsonPropertyName("Id")] public string Id { get; set; } = string.Empty;
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Type")] public string? Type { get; set; }
        [JsonPropertyName("Path")] public string? Path { get; set; }
        [JsonPropertyName("IndexNumber")] public int? IndexNumber { get; set; }
        [JsonPropertyName("ProviderIds")] public Dictionary<string, string>? ProviderIds { get; set; }
        [JsonPropertyName("UserData")] public JfUserData? UserData { get; set; }
    }
    private sealed class JfUserData { [JsonPropertyName("Played")] public bool Played { get; set; } }

    // ── MAL JSON DTOs ──────────────────────────────────────────────────
    private sealed class MalListPage
    {
        [JsonPropertyName("data")] public List<MalListEntry>? Data { get; set; }
        [JsonPropertyName("paging")] public MalPaging? Paging { get; set; }
    }
    private sealed class MalListEntry
    {
        [JsonPropertyName("node")] public MalNode Node { get; set; } = new();
        [JsonPropertyName("list_status")] public MalListStatus? ListStatus { get; set; }
    }
    private sealed class MalNode
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("num_episodes")] public int NumEpisodes { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
        [JsonPropertyName("alternative_titles")] public MalAltTitles? AlternativeTitles { get; set; }
    }
    private sealed class MalAltTitles
    {
        [JsonPropertyName("en")] public string? En { get; set; }
        [JsonPropertyName("synonyms")] public List<string>? Synonyms { get; set; }
    }
    private sealed class MalListStatus
    {
        [JsonPropertyName("num_episodes_watched")] public int NumEpisodesWatched { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
    private sealed class MalPaging { [JsonPropertyName("next")] public string? Next { get; set; } }

    private sealed class MalAnimeInfo
    {
        [JsonPropertyName("num_episodes")] public int NumEpisodes { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }

    private sealed class MalRelatedResponse
    {
        [JsonPropertyName("related_anime")] public List<MalRelatedEntry>? RelatedAnime { get; set; }
    }
    private sealed class MalRelatedEntry
    {
        [JsonPropertyName("node")] public MalNode Node { get; set; } = new();
        [JsonPropertyName("relation_type")] public string? RelationType { get; set; }
    }

    private sealed class MalSearchPage { [JsonPropertyName("data")] public List<MalSearchEntry>? Data { get; set; } }
    private sealed class MalSearchEntry { [JsonPropertyName("node")] public MalNode Node { get; set; } = new(); }
}
