using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
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
    private readonly string _cacheFilePath;

    // In-memory runtime cache (keyed: userId::normalizedTitle::season)
    private readonly Dictionary<string, CacheEntry> _malIdCache = new();
    private readonly Dictionary<string, SyncState> _syncState = new();

    // Persistent cache loaded from disk
    private readonly Dictionary<string, CacheEntry> _persistentCache = new();
    private bool _persistentCacheLoaded = false;
    private readonly SemaphoreSlim _cacheSaveLock = new(1, 1);

    private static readonly JsonSerializerOptions _jsonOpts = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true,
    };

    public MalSyncService(
        IHttpClientFactory httpFactory,
        MalAuthService auth,
        ILibraryManager libraryManager,
        IUserDataManager userDataManager,
        IUserManager userManager,
        IApplicationPaths appPaths,
        ILogger<MalSyncService> logger)
    {
        _httpFactory = httpFactory;
        _auth = auth;
        _libraryManager = libraryManager;
        _userDataManager = userDataManager;
        _userManager = userManager;
        _logger = logger;
        _cacheFilePath = Path.Combine(appPaths.DataPath, "plugins", "MalSync", "cache.json");
    }

    // ═════════════════════════════════════════════════════════════════════
    // PUBLIC ENTRY POINT
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Runs a full sync for one Jellyfin user.
    /// </summary>
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

        EnsurePersistentCacheLoaded();

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
        // Track seasons that could not be matched for end-of-run summary
        var unresolved = new List<string>();

        Log(dryRun ? "[DRY RUN – no changes will be written to MAL]" : "Starting sync…");

        foreach (var series in animeSeries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var seriesName = series.Name ?? "Unknown";
            var seriesId = series.Id;

            // Load seasons — include Season 0 only when it has a pinned override
            var seasons = GetSeasons(Guid.Parse(seriesId), jfUser);
            var realSeasons = seasons.Where(s =>
            {
                var n = s.IndexNumber ?? 0;
                if (n >= 1) return true;
                if (n == 0)
                {
                    var ov = GetSyncOverride(userCfg, seriesId, 0);
                    return ov?.PinnedMalId is not null && ov.Blocked != true;
                }
                return false;
            }).ToList();
            if (realSeasons.Count == 0) { Dbg($"No processable seasons for '{seriesName}', skipping."); continue; }

            // ── Detect seasons that share the same pinned MAL ID ──────────────────
            // e.g. Zexal S1+S2+S3 all pinned → "Yu-Gi-Oh! Zexal" → aggregate watched counts
            var pinnedGroupMap = userCfg.SeriesOverrides
                .Where(o => o.JellyfinSeriesId == seriesId
                         && o.Blocked != true
                         && !string.IsNullOrEmpty(o.PinnedMalId))
                .GroupBy(o => o.PinnedMalId!, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(o => o.SeasonNumber).ToHashSet(),
                    StringComparer.OrdinalIgnoreCase);
            var handledAggregatedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var season in realSeasons)
            {
                var seasonNum = season.IndexNumber ?? 1;
                var seasonId = season.Id;
                var normalizedSeriesName = NormalizeTitle(seriesName);

                // ── Check episode-range mappings (absolute-numbered shows) ────
                var rangeMapping = userCfg.EpisodeRangeMappings
                    .FirstOrDefault(m => m.JellyfinSeriesId == seriesId && m.SeasonNumber == seasonNum);

                if (rangeMapping is not null && rangeMapping.Ranges.Count > 0)
                {
                    await ApplyRangeMappingAsync(seriesName, seasonId, seasonNum, seriesId,
                        rangeMapping, malUserList, jfUser, effectiveNoDowngrade,
                        dryRun, malHeaders, Log, Dbg,
                        notice =>
                        {
                            // Replace any existing notice for the same series+season (no duplicates)
                            userCfg.StaleRangeNotices.RemoveAll(n =>
                                n.JellyfinSeriesId == notice.JellyfinSeriesId &&
                                n.SeasonNumber     == notice.SeasonNumber);
                            userCfg.StaleRangeNotices.Add(notice);
                            MalSyncPlugin.Instance!.SaveConfiguration();

                            // Send webhook notification if configured and enabled
                            if (!string.IsNullOrEmpty(userCfg.WebhookUrl) && userCfg.WebhookOnStaleRanges)
                            {
                                var msg = $"**{notice.JellyfinSeriesName}** — season {notice.SeasonNumber}\n" +
                                          $"Jellyfin has more episodes than **{notice.MalTitle}** covers on MyAnimeList.\n" +
                                          $"A new part has probably aired, so the split needs extending.\n\n" +
                                          $"*Open MAL Sync → Library to review it.*";
                                _ = SendWebhookAsync(userCfg.WebhookUrl,
                                    "⚠️ MAL Sync: a season split looks out of date", msg);
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // ── Check user overrides first ─────────────────────────
                var syncOverride = GetSyncOverride(userCfg, seriesId, seasonNum);
                if (syncOverride is not null)
                {
                    if (syncOverride.Blocked)
                    {
                        Dbg($"Skipping '{seriesName}' S{seasonNum}: blocked by user override.");
                        continue;
                    }
                    if (!string.IsNullOrEmpty(syncOverride.PinnedMalId))
                    {
                        var malId = syncOverride.PinnedMalId;

                        // ── Multi-season aggregation: several Jellyfin seasons → same MAL entry ──
                        if (pinnedGroupMap.TryGetValue(malId, out var groupSeasonNums)
                            && groupSeasonNums.Contains(seasonNum))
                        {
                            if (!handledAggregatedGroups.Add(malId))
                            {
                                Dbg($"'{seriesName}' S{seasonNum}: aggregated with group for MAL {malId} (already handled).");
                            }
                            else
                            {
                                var groupSeasons = realSeasons
                                    .Where(s => groupSeasonNums.Contains(s.IndexNumber ?? 0))
                                    .Select(s => (sid: s.Id, snum: s.IndexNumber ?? 0))
                                    .OrderBy(s => s.snum)
                                    .ToList();
                                Dbg($"'{seriesName}': aggregating {groupSeasons.Count} seasons → MAL ID {malId}.");
                                await SyncAggregatedGroupAsync(
                                    seriesId, seriesName, malId, groupSeasons,
                                    malUserList, jfUser,
                                    effectiveNoDowngrade, effectiveJfUpdateWatched,
                                    dryRun, malHeaders, cacheScope,
                                    Log, Dbg, cancellationToken).ConfigureAwait(false);
                            }
                            continue;
                        }

                        // ── Normal single-season pin ──────────────────────────────────────────
                        Dbg($"Using pinned MAL ID {malId} for '{seriesName}' S{seasonNum}.");
                        if (seasonNum == 1) s1IdCache.TryAdd(seriesId, malId);
                        await ProcessSeasonAsync(
                            jellyfinUserId, seriesId, seriesName, seasonId, seasonNum, realSeasons.Count,
                            malId, malUserList, jfUser, effectiveNoDowngrade, effectiveJfUpdateWatched,
                            dryRun, malHeaders, cacheScope, normalizedSeriesName, s1IdCache,
                            Log, Dbg, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                // Evidence beyond the title, used to separate candidates that score
                // alike: how many episodes this season actually has and what year it
                // is from. Computed lazily — it only matters on a cache miss.
                MatchHints? hintsCache = null;
                MatchHints Hints()
                {
                    if (hintsCache is null)
                    {
                        var epCount = GetEpisodes(Guid.Parse(seasonId), jfUser).Count;
                        if (epCount == 0)
                            epCount = GetEpisodesBySeriesAndSeason(Guid.Parse(seriesId), seasonNum, jfUser).Count;
                        hintsCache = new MatchHints(epCount, season.ProductionYear ?? series.ProductionYear);
                    }
                    return hintsCache.Value;
                }

                // ── Resolve MAL ID ─────────────────────────────────────
                string? malId2 = season.ProviderIds?.GetValueOrDefault("MyAnimeList");
                if (malId2 is not null)
                    Dbg($"Using Jellyfin season provider MAL ID {malId2} for '{seriesName}' S{seasonNum}.");

                if (malId2 is null)
                {
                    malId2 = GetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, cfg.CacheTtlDays);
                    if (malId2 is not null)
                        Dbg($"Using cached MAL ID {malId2} for '{seriesName}' S{seasonNum}.");
                }
                if (malId2 is not null && seasonNum == 1) s1IdCache.TryAdd(seriesId, malId2);

                if (malId2 is null)
                {
                    malId2 = FindIdInUserList(malTitleEntries, seriesName, seasonNum, cfg.MalSearchMinSimilarity);
                    if (malId2 is not null)
                    {
                        Dbg($"Using MAL user-list match ID {malId2} for '{seriesName}' S{seasonNum}.");
                        if (seasonNum == 1) s1IdCache.TryAdd(seriesId, malId2);
                        SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId2,
                            malUserList.TryGetValue(malId2, out var uEntry) ? uEntry.Title : null);
                    }
                }

                if (malId2 is null)
                {
                    if (seasonNum == 1 || seasonNum == 0)
                    {
                        malId2 = series.ProviderIds?.GetValueOrDefault("MyAnimeList");
                        MalMatch? match = null;
                        if (malId2 is null)
                        {
                            // Primary search: full series name
                            Dbg($"No MAL ID for '{seriesName}' S{seasonNum}, searching by title…");
                            match = await SearchMalMatchAsync(seriesName, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken, hints: Hints()).ConfigureAwait(false);
                        }

                        // Fallback 1: strip subtitle after ":"
                        if (malId2 is null && match is null && seriesName.Contains(':'))
                        {
                            var noSubtitle = seriesName[..seriesName.IndexOf(':')].Trim();
                            if (noSubtitle.Length >= 3)
                            {
                                Dbg($"  Fallback search without subtitle: '{noSubtitle}'…");
                                match = await SearchMalMatchAsync(noSubtitle, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken, hints: Hints()).ConfigureAwait(false);
                            }
                        }

                        // Fallback 2: strip trailing season/part suffix
                        if (malId2 is null && match is null)
                        {
                            var stripped = StripSeasonSuffix(seriesName);
                            if (stripped.Length >= 3 && stripped != seriesName)
                            {
                                Dbg($"  Fallback search stripped suffix: '{stripped}'…");
                                match = await SearchMalMatchAsync(stripped, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken, hints: Hints()).ConfigureAwait(false);
                            }
                        }

                        malId2 ??= match?.Id;

                        if (malId2 is not null)
                        {
                            if (seasonNum == 1) s1IdCache.TryAdd(seriesId, malId2);
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId2,
                                malUserList.TryGetValue(malId2, out var uEntry2) ? uEntry2.Title : match?.Title,
                                match?.ImageUrl,
                                match?.Episodes ?? 0);
                        }
                    }
                    else
                    {
                        s1IdCache.TryGetValue(seriesId, out var s1Id);
                        if (s1Id is null)
                        {
                            var baseTitle = StripSeasonSuffix(seriesName);
                            Dbg($"No S1 cache for '{seriesName}', searching S1 by title '{baseTitle}'…");
                            s1Id = await SearchMalIdAsync(baseTitle, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken,
                                hints: new MatchHints(0, series.ProductionYear)).ConfigureAwait(false);
                        }
                        if (s1Id is not null)
                        {
                            Dbg($"Traversing sequel chain for '{seriesName}' S{seasonNum} from S1 ID {s1Id}…");
                            malId2 = await GetMalSequelFromChainAsync(s1Id, seasonNum, seriesName, malHeaders, cancellationToken).ConfigureAwait(false);
                        }
                        MalMatch? seqMatch = null;
                        if (malId2 is null)
                        {
                            var suffix = seasonNum switch { 2 => "2nd Season", 3 => "3rd Season", 4 => "4th Season", 5 => "5th Season", _ => $"{seasonNum}th Season" };
                            Dbg($"Sequel chain failed, direct search for '{seriesName} {suffix}'…");
                            seqMatch = await SearchMalMatchAsync($"{seriesName} {suffix}", malHeaders, seasonNum, cfg.MalSearchMinSimilarity, cancellationToken, hints: Hints()).ConfigureAwait(false);
                            malId2 = seqMatch?.Id;
                        }
                        if (malId2 is not null)
                        {
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId2,
                                malUserList.TryGetValue(malId2, out var uEntry3) ? uEntry3.Title : seqMatch?.Title,
                                seqMatch?.ImageUrl,
                                seqMatch?.Episodes ?? 0);
                        }
                    }
                }

                // Guard against S1 resolving to sequel IDs (e.g. "... 2", "... Ni!").
                if (seasonNum == 1 && malId2 is not null)
                {
                    var looksLikeSequel = await IsLikelySequelCandidateAsync(
                        malId2, malUserList, malHeaders, cancellationToken).ConfigureAwait(false);

                    if (looksLikeSequel)
                    {
                        Dbg($"Rejecting S1 candidate MAL ID {malId2} for '{seriesName}' because the title looks like a sequel. Retrying without this ID.");

                        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { malId2 };
                        var remapped = FindIdInUserList(malTitleEntries, seriesName, seasonNum, cfg.MalSearchMinSimilarity, excluded);

                        if (remapped is null)
                        {
                            Dbg($"No usable MAL list match left for '{seriesName}' S1, searching title with exclusion…");
                            remapped = await SearchMalIdAsync(seriesName, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken, excluded, Hints()).ConfigureAwait(false);
                        }

                        if (remapped is not null)
                        {
                            malId2 = remapped;
                            s1IdCache[seriesId] = malId2;
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId2,
                                malUserList.TryGetValue(malId2, out var uEntry4) ? uEntry4.Title : null);
                            Dbg($"S1 remap for '{seriesName}': using MAL ID {malId2} after sequel rejection.");
                        }
                        else
                        {
                            Dbg($"Skipping '{seriesName}' S1: only sequel-like MAL candidates were found.");
                            malId2 = null;
                        }
                    }
                }

                if (malId2 is not null && seasonNum == 1) s1IdCache.TryAdd(seriesId, malId2);

                if (malId2 is null)
                {
                    SetCachedNoMatch(cacheScope, normalizedSeriesName, seasonNum);
                    var unresolvedLabel = seasonNum == 0
                        ? $"{seriesName} [Specials]"
                        : $"{seriesName} S{seasonNum}";
                    Dbg($"Skipping '{unresolvedLabel}': MAL ID not found.");
                    unresolved.Add(unresolvedLabel);
                    continue;
                }

                // ── Hint: suggest range mapping when episodes don't match ──
                // Auto-saving ranges during sync is intentionally NOT done — it produced
                // false positives (e.g. Tomozaki S2, Ramparts of Ice with 1-ep MAL chain entries).
                // The season is flagged instead; the Library tab offers the split there.
                if (malUserList.TryGetValue(malId2, out var checkListEntry) && checkListEntry.Total > 0)
                {
                    var jfEpCount = GetEpisodes(Guid.Parse(seasonId), jfUser).Count;
                    if (jfEpCount == 0)
                        jfEpCount = GetEpisodesBySeriesAndSeason(Guid.Parse(seriesId), seasonNum, jfUser).Count;

                    if (jfEpCount > checkListEntry.Total * 2)
                    {
                        var label = seasonNum == 0 ? $"{seriesName} [Specials]" : $"{seriesName} S{seasonNum}";
                        Log($"[WARN] '{label}': Jellyfin has {jfEpCount} episodes but MAL entry only has {checkListEntry.Total}. " +
                            "If one Jellyfin season covers several MAL entries, open MAL Sync \u2192 Library and split it.");
                    }
                }

                await ProcessSeasonAsync(
                    jellyfinUserId, seriesId, seriesName, seasonId, seasonNum, realSeasons.Count,
                    malId2, malUserList, jfUser, effectiveNoDowngrade, effectiveJfUpdateWatched,
                    dryRun, malHeaders, cacheScope, normalizedSeriesName, s1IdCache,
                    Log, Dbg, cancellationToken).ConfigureAwait(false);
            }
        }

        if (unresolved.Count > 0)
        {
            Log($"[WARN] {unresolved.Count} season(s) could not be matched to MAL — open MAL Sync \u2192 Library and press Fix to choose an entry:");
            foreach (var u in unresolved)
                Log($"[WARN]  ⚠ {u}");
        }

        Log(dryRun ? "Dry-run complete." : "Sync complete.");

        // ── Post-sync webhook notifications ───────────────────────────────
        if (!string.IsNullOrEmpty(userCfg.WebhookUrl))
        {
            if (userCfg.WebhookOnSyncErrors)
            {
                var errors = log
                    .Where(l => l.StartsWith("[MAL ERROR]") || (l.StartsWith("[ERROR]") && !l.Contains("Not authenticated")))
                    .ToList();
                if (errors.Count > 0)
                {
                    var desc = $"**{errors.Count} problem(s)** during the sync:\n" +
                               string.Join("\n", errors.Take(5).Select(l => $"• `{l}`"));
                    if (errors.Count > 5) desc += $"\n_and {errors.Count - 5} more_";
                    _ = SendWebhookAsync(userCfg.WebhookUrl, "❌ MAL Sync: sync problems", desc);
                }
            }

            if (userCfg.WebhookOnSyncSummary && !dryRun)
            {
                var updates = log.Where(l => l.StartsWith("[MAL] ")).ToList();
                if (updates.Count > 0)
                {
                    var desc = $"**{updates.Count} entries** updated on MyAnimeList:\n" +
                               string.Join("\n", updates.Take(10).Select(l => $"• {l.Replace("[MAL] ", "")}"));
                    if (updates.Count > 10) desc += $"\n_and {updates.Count - 10} more_";
                    _ = SendWebhookAsync(userCfg.WebhookUrl, "✅ MAL Sync: progress sent", desc);
                }
            }
        }

        return log;
    }

    // ── Episode-range sync (multiple MAL entries per Jellyfin season) ────────
    private async Task ApplyRangeMappingAsync(
        string seriesName, string seasonId, int seasonNum, string seriesId,
        Configuration.EpisodeRangeMapping rangeMapping,
        Dictionary<string, MalUserEntry> malUserList, User jfUser,
        bool effectiveNoDowngrade, bool dryRun,
        Dictionary<string, string> malHeaders,
        Action<string> log, Action<string> dbg,
        Action<Configuration.StaleRangeNotice>? onNotice,
        CancellationToken cancellationToken)
    {
        var episodes = GetEpisodes(Guid.Parse(seasonId), jfUser);
        if (episodes.Count == 0)
            episodes = GetEpisodesBySeriesAndSeason(Guid.Parse(seriesId), seasonNum, jfUser);

        var minIdx = episodes.Count > 0 ? episodes.Min(e => e.IndexNumber ?? 1) : 1;
        var seasonOffset = minIdx > 12 ? minIdx - 1 : 0;

        foreach (var range in rangeMapping.Ranges.OrderBy(r => r.EpisodeFrom))
        {
            if (string.IsNullOrEmpty(range.MalId)) continue;

            var rangeEps = episodes.Where(e =>
            {
                var idx = (e.IndexNumber ?? 0) - seasonOffset;
                return idx >= range.EpisodeFrom && (range.EpisodeTo == 0 || idx <= range.EpisodeTo);
            }).ToList();

            var rangeLabel = $"ep{range.EpisodeFrom}–{(range.EpisodeTo == 0 ? "∞" : range.EpisodeTo.ToString())}";

            if (rangeEps.Count == 0) { dbg($"  Range {rangeLabel}: no episodes found."); continue; }

            var watchedInRange = rangeEps
                .Where(e => e.UserData?.Played == true)
                .Select(e => (e.IndexNumber ?? 0) - seasonOffset - range.EpisodeFrom + 1)
                .DefaultIfEmpty(0).Max();

            if (watchedInRange <= 0) { dbg($"  Range {rangeLabel}: nothing watched yet."); continue; }

            malUserList.TryGetValue(range.MalId, out var malEntry);
            var malTotal = malEntry?.Total ?? 0;
            var airingStatus = malEntry?.AiringStatus ?? string.Empty;
            if (malEntry is null)
            {
                var info = await GetMalAnimeInfoAsync(range.MalId, malHeaders, cancellationToken).ConfigureAwait(false);
                malTotal = info.NumEpisodes;
                airingStatus = info.Status ?? string.Empty;
            }

            var rawWatchedInRange = watchedInRange;
            if (malTotal > 0) watchedInRange = Math.Min(watchedInRange, malTotal);
            var status = airingStatus == "finished_airing" && malTotal > 0 && watchedInRange >= malTotal
                         ? "completed" : "watching";
            var label = $"{seriesName} {rangeLabel} ({range.MalTitle ?? range.MalId})";

            // Warn when the season has more watched episodes than this range's MAL entry can hold.
            // This typically means a new sequel part is airing but ranges haven't been updated yet.
            if (range.EpisodeTo == 0 && malTotal > 0 && rawWatchedInRange > malTotal
                && airingStatus == "finished_airing")
            {
                log($"[WARN] {label}: {rawWatchedInRange} episodes watched but MAL only has {malTotal}. " +
                    $"A new part has probably aired — open MAL Sync \u2192 Library to extend the split.");
                onNotice?.Invoke(new Configuration.StaleRangeNotice
                {
                    JellyfinSeriesId   = rangeMapping.JellyfinSeriesId,
                    JellyfinSeriesName = rangeMapping.JellyfinSeriesName,
                    SeasonNumber       = rangeMapping.SeasonNumber,
                    MalTitle           = range.MalTitle ?? range.MalId,
                    DetectedAt         = DateTime.UtcNow,
                });
            }

            if (malEntry is not null && effectiveNoDowngrade && watchedInRange < malEntry.Watched)
            { dbg($"  → '{label}': skip (would downgrade)."); continue; }
            if (malEntry?.Watched == watchedInRange && malEntry?.Status == status)
            { dbg($"  → '{label}': already up to date."); continue; }

            if (dryRun)
            {
                log($"[DRY RUN] {label}: would set {watchedInRange}/{(malTotal > 0 ? malTotal : "?")} ({status})");
            }
            else
            {
                using var http = _httpFactory.CreateClient("MalSync");
                foreach (var (k, v) in malHeaders) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
                var resp = await http.PutAsync(
                    $"https://api.myanimelist.net/v2/anime/{range.MalId}/my_list_status",
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["num_watched_episodes"] = watchedInRange.ToString(),
                        ["status"] = status,
                    }), cancellationToken).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                    log($"[MAL] {label}: {watchedInRange}/{(malTotal > 0 ? malTotal : "?")} eps ({status})");
                else
                    log($"[MAL ERROR] Range sync failed for '{label}': {await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)}");
            }
        }
    }

    // ── Core per-season sync logic (extracted to reduce nesting) ──────────
    private async Task ProcessSeasonAsync(
        string jellyfinUserId, string seriesId, string seriesName, string seasonId, int seasonNum, int totalRealSeasons,
        string malId, Dictionary<string, MalUserEntry> malUserList, User jfUser,
        bool effectiveNoDowngrade, bool effectiveJfUpdateWatched,
        bool dryRun, Dictionary<string, string> malHeaders, string cacheScope,
        string normalizedSeriesName, Dictionary<string, string> s1IdCache,
        Action<string> log, Action<string> dbg,
        CancellationToken cancellationToken)
    {
        // ── Get MAL entry info ─────────────────────────────────────────
        malUserList.TryGetValue(malId, out var malEntry);
        int malTotal = malEntry?.Total ?? 0;
        var airingStatus = malEntry?.AiringStatus ?? string.Empty;
        string? malDisplayTitle = malEntry?.Title;
        string? malImageUrl = null;

        if (malEntry is null)
        {
            var info = await GetMalAnimeInfoAsync(malId, malHeaders, cancellationToken).ConfigureAwait(false);
            malTotal = info.NumEpisodes;
            airingStatus = info.Status ?? string.Empty;
            malDisplayTitle = info.AlternativeTitles?.En is { Length: > 0 } en ? en : info.Title;
            malImageUrl = info.MainPicture?.Medium ?? info.MainPicture?.Large;
        }
        dbg($"'{seriesName}' S{seasonNum} → MAL ID {malId}, eps: {(malTotal > 0 ? malTotal : "?")}, airing: {airingStatus}");

        // ── Load Jellyfin episodes ─────────────────────────────────────
        var episodes = GetEpisodes(Guid.Parse(seasonId), jfUser);
        if (episodes.Count == 0)
        {
            dbg($"  → '{seriesName}' S{seasonNum}: no episodes found under season ID {seasonId}, trying series-level fallback…");
            episodes = GetEpisodesBySeriesAndSeason(Guid.Parse(seriesId), seasonNum, jfUser);
            if (episodes.Count == 0)
            {
                dbg($"  → '{seriesName}' S{seasonNum}: no episodes found at series level either, skipping.");
                return;
            }
            dbg($"  → '{seriesName}' S{seasonNum}: series-level fallback returned {episodes.Count} episode(s).");
        }

        // Remember what we learned about the MAL entry, so the library view can name it
        // and flag a season that has outgrown it without asking MAL again.
        UpdateCachedDetails(cacheScope, normalizedSeriesName, seasonNum, malTotal, malDisplayTitle, malImageUrl);

        // ── One Jellyfin season, several MAL entries ───────────────────
        // The classic shape for long-running shows: Jellyfin keeps everything under
        // one season while MAL splits it into cours. Syncing that against a single
        // entry silently caps progress, so surface it instead of failing quietly.
        if (malTotal > 0 && episodes.Count >= 12 && episodes.Count > malTotal * 1.5)
        {
            var uc = _auth.GetOrCreateUserConfig(jellyfinUserId);
            var hasRanges = uc.EpisodeRangeMappings.Any(m =>
                m.JellyfinSeriesId == seriesId && m.SeasonNumber == seasonNum
                && m.Ranges.Count > 0);

            if (!hasRanges)
            {
                var already = uc.StaleRangeNotices.Any(n =>
                    n.JellyfinSeriesId == seriesId && n.SeasonNumber == seasonNum && n.Kind == "split");

                if (!already)
                {
                    dbg($"'{seriesName}' S{seasonNum}: {episodes.Count} episodes but MAL entry has {malTotal} — working out the split…");

                    // Do the work now rather than waiting for the user to ask: the
                    // notice then arrives with a split they only have to accept.
                    var suggested = await DetectEpisodeRangesAsync(
                        malId, jellyfinUserId, cancellationToken,
                        Guid.TryParse(seriesId, out var sGuid) ? sGuid : null,
                        seasonNum, jfUser).ConfigureAwait(false);

                    uc.StaleRangeNotices.RemoveAll(n =>
                        n.JellyfinSeriesId == seriesId && n.SeasonNumber == seasonNum);
                    uc.StaleRangeNotices.Add(new Configuration.StaleRangeNotice
                    {
                        Kind = "split",
                        JellyfinSeriesId = seriesId,
                        JellyfinSeriesName = seriesName,
                        SeasonNumber = seasonNum,
                        MalTitle = malDisplayTitle ?? $"MAL {malId}",
                        SuggestedRanges = suggested.Count >= 2
                            ? suggested.Select(r => new Configuration.EpisodeRange
                            {
                                EpisodeFrom = r.EpisodeFrom,
                                EpisodeTo = r.EpisodeTo,
                                MalId = r.MalId,
                                MalTitle = r.MalTitle,
                                MalImageUrl = r.MalImageUrl,
                            }).ToList()
                            : new(),
                    });
                    MalSyncPlugin.Instance!.SaveConfiguration();

                    dbg(suggested.Count >= 2
                        ? $"  → proposed {suggested.Count} parts for '{seriesName}' S{seasonNum}."
                        : $"  → could not identify the parts for '{seriesName}' S{seasonNum}; asking the user.");
                }
            }
        }

        // Season offset for absolute-numbered shows
        var minIdx = episodes.Min(e => e.IndexNumber ?? 1);
        var seasonOffset = minIdx > 12 ? minIdx - 1 : 0;

        var label = (seasonNum > 1 || totalRealSeasons > 1) ? $"{seriesName} S{seasonNum}" : seriesName;

        // ── MAL → Jellyfin: mark episodes played ───────────────
        if (effectiveJfUpdateWatched && malEntry?.Watched > 0)
        {
            MarkJfWatched(jfUser, episodes, malEntry.Watched, seasonOffset, label, dryRun);
        }

        // ── Calculate watched count ────────────────────────────
        var watchedEps = episodes.Where(e => e.UserData?.Played == true).ToList();
        if (watchedEps.Count == 0) { dbg($"  → '{label}': no episodes watched yet."); return; }

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
                    dbg($"  → '{label}': skipping – would downgrade MAL (local {watchedCount} {status} | MAL {malEntry.Watched} {malEntry.Status}).");
                    return;
                }
            }
            if (malEntry.Watched == watchedCount && malEntry.Status == status)
            {
                dbg($"  → '{label}': already up to date ({watchedCount} eps, {status}).");
                return;
            }
        }
        else
        {
            var syncStateKey = $"{cacheScope}::{malId}";
            if (_syncState.TryGetValue(syncStateKey, out var last)
                && last.WatchedCount == watchedCount && last.Status == status)
            {
                dbg($"  → '{label}': no change since last run, skipping.");
                return;
            }
        }

        // ── Write to MAL (or dry-run) ──────────────────────────
        if (dryRun)
        {
            if (malEntry is not null)
                log($"[DRY RUN] {label}: would set ep {watchedCount}/{(malTotal > 0 ? malTotal : "?")} ({status})" +
                    $" – MAL currently has {malEntry.Watched}/{(malTotal > 0 ? malTotal : "?")} ({malEntry.Status}) [ID {malId}]");
            else
                log($"[DRY RUN] {label}: would set ep {watchedCount}/{(malTotal > 0 ? malTotal : "?")} ({status})" +
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
                log($"[MAL] {label}: {watchedCount}/{(malTotal > 0 ? malTotal : "?")} eps ({status})");
                _syncState[$"{cacheScope}::{malId}"] = new SyncState(watchedCount, status);
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                log($"[MAL ERROR] Could not sync '{label}': {body}");
            }
        }
    }

    // ── Aggregated sync: multiple Jellyfin seasons → one MAL entry ──────────
    // Used when several seasons (e.g. S1+S2+S3) are all pinned to the same MAL ID
    // because the streaming platform split one MAL entry across multiple Jellyfin seasons.
    private async Task SyncAggregatedGroupAsync(
        string seriesId, string seriesName, string malId,
        List<(string sid, int snum)> groupSeasons,
        Dictionary<string, MalUserEntry> malUserList, User jfUser,
        bool effectiveNoDowngrade, bool effectiveJfUpdateWatched,
        bool dryRun, Dictionary<string, string> malHeaders, string cacheScope,
        Action<string> log, Action<string> dbg,
        CancellationToken cancellationToken)
    {
        // ── MAL metadata ─────────────────────────────────────────────────
        malUserList.TryGetValue(malId, out var malEntry);
        var malTotal = malEntry?.Total ?? 0;
        var airingStatus = malEntry?.AiringStatus ?? string.Empty;
        if (malEntry is null)
        {
            var info = await GetMalAnimeInfoAsync(malId, malHeaders, cancellationToken).ConfigureAwait(false);
            malTotal = info.NumEpisodes;
            airingStatus = info.Status ?? string.Empty;
        }

        var seasonLabels = string.Join("+S", groupSeasons.Select(s => s.snum));
        var label = $"{seriesName} [S{seasonLabels} → MAL {malId}]";
        dbg($"Aggregated group {label}: MAL total={malTotal}, airing={airingStatus}");

        // ── Load episodes per season and accumulate watched ──────────────
        int totalWatched = 0;
        var perSeasonInfo = new List<(List<JfItem> eps, int offset, int epCount)>();

        foreach (var (sid, snum) in groupSeasons)
        {
            var eps = GetEpisodes(Guid.Parse(sid), jfUser);
            if (eps.Count == 0)
                eps = GetEpisodesBySeriesAndSeason(Guid.Parse(seriesId), snum, jfUser);
            if (eps.Count == 0)
            {
                dbg($"  S{snum}: no episodes found, skipping.");
                perSeasonInfo.Add((new List<JfItem>(), 0, 0));
                continue;
            }

            var minIdx = eps.Min(e => e.IndexNumber ?? 1);
            var offset = minIdx > 12 ? minIdx - 1 : 0;
            var epCount = eps.Max(e => e.IndexNumber ?? 0) - offset; // total episodes in this Jellyfin season

            var watchedInSeason = eps
                .Where(e => e.UserData?.Played == true)
                .Select(e => (e.IndexNumber ?? 0) - offset)
                .DefaultIfEmpty(0).Max();

            dbg($"  S{snum}: {watchedInSeason}/{epCount} watched (idx offset {offset}).");
            totalWatched += watchedInSeason;
            perSeasonInfo.Add((eps, offset, epCount));
        }

        if (totalWatched == 0) { dbg($"  → {label}: nothing watched yet."); return; }
        if (malTotal > 0) totalWatched = Math.Min(totalWatched, malTotal);

        var status = airingStatus == "finished_airing" && malTotal > 0 && totalWatched >= malTotal
                     ? "completed" : "watching";

        // ── MAL → Jellyfin: mark episodes as played ──────────────────────
        // Walk seasons in order; each season's episodes correspond to MAL episodes
        // [cumulativeOffset+1 .. cumulativeOffset+seasonEpCount].
        if (effectiveJfUpdateWatched && malEntry?.Watched > 0)
        {
            var cumOffset = 0;
            foreach (var (eps, offset, epCount) in perSeasonInfo)
            {
                if (eps.Count == 0 || epCount == 0) continue;
                // How many MAL-watched episodes fall into this season?
                var watchedInThisSeason = Math.Max(0, Math.Min(malEntry.Watched - cumOffset, epCount));
                if (watchedInThisSeason > 0)
                    MarkJfWatched(jfUser, eps, watchedInThisSeason, offset, label, dryRun);
                cumOffset += epCount;
                if (cumOffset >= malEntry.Watched) break;
            }
        }

        // ── Change detection ─────────────────────────────────────────────
        if (malEntry is not null)
        {
            if (effectiveNoDowngrade)
            {
                var rank = new Dictionary<string, int>
                { ["completed"] = 3, ["watching"] = 2, ["on_hold"] = 1, ["plan_to_watch"] = 0, ["dropped"] = 0 };
                if (totalWatched < malEntry.Watched
                    || rank.GetValueOrDefault(status) < rank.GetValueOrDefault(malEntry.Status ?? ""))
                {
                    dbg($"  → {label}: skip (would downgrade: local {totalWatched} {status} vs MAL {malEntry.Watched} {malEntry.Status}).");
                    return;
                }
            }
            if (malEntry.Watched == totalWatched && malEntry.Status == status)
            {
                dbg($"  → {label}: already up to date ({totalWatched} eps, {status}).");
                return;
            }
        }
        else
        {
            var stateKey = $"{cacheScope}::{malId}";
            if (_syncState.TryGetValue(stateKey, out var last)
                && last.WatchedCount == totalWatched && last.Status == status)
            {
                dbg($"  → {label}: no change since last run.");
                return;
            }
        }

        // ── Write to MAL ─────────────────────────────────────────────────
        if (dryRun)
        {
            if (malEntry is not null)
                log($"[DRY RUN] {label}: would set {totalWatched}/{(malTotal > 0 ? malTotal : "?")} ({status})" +
                    $" – MAL has {malEntry.Watched}/{(malTotal > 0 ? malTotal : "?")} ({malEntry.Status})");
            else
                log($"[DRY RUN] {label}: would set {totalWatched}/{(malTotal > 0 ? malTotal : "?")} ({status}) – not in MAL list yet");
        }
        else
        {
            using var http = _httpFactory.CreateClient("MalSync");
            foreach (var (k, v) in malHeaders) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);
            var resp = await http.PutAsync(
                $"https://api.myanimelist.net/v2/anime/{malId}/my_list_status",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["num_watched_episodes"] = totalWatched.ToString(),
                    ["status"] = status,
                }),
                cancellationToken).ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                log($"[MAL] {label}: {totalWatched}/{(malTotal > 0 ? malTotal : "?")} eps ({status})");
                _syncState[$"{cacheScope}::{malId}"] = new SyncState(totalWatched, status);
            }
            else
            {
                var body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                log($"[MAL ERROR] Aggregated sync failed for {label}: {body}");
            }
        }
    }

    // ═════════════════════════════════════════════════════════════════════
    // PUBLIC MANAGEMENT METHODS
    // ═════════════════════════════════════════════════════════════════════

    /// <summary>Returns all Jellyfin anime series with their current MAL ID mappings and overrides.</summary>
    public List<SeriesMapping> GetSeriesMappings(string userId)
    {
        EnsurePersistentCacheLoaded();

        var cfg = MalSyncPlugin.Instance!.Configuration;
        var userCfg = _auth.GetOrCreateUserConfig(userId);
        var animePaths = cfg.GetAnimePaths();

        var jfUser = _userManager.GetUserById(Guid.Parse(userId));
        if (jfUser is null) return new();

        // Episode counts for the whole library in a single query — one query per
        // season would be hundreds of round-trips on a real anime library.
        var episodeCounts = GetEpisodeCountsBySeason(jfUser);

        var jfItems = GetJfItems(jfUser);
        var animeSeries = jfItems
            .Where(i => i.Type == "Series"
                     && animePaths.Any(p => (i.Path ?? "").StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(i => i.Name)
            .ToList();

        var result = new List<SeriesMapping>();

        foreach (var series in animeSeries)
        {
            var seriesId = series.Id;
            var seasons = GetSeasons(Guid.Parse(seriesId), jfUser);
            // Include Season 0 (specials/OVAs) — displayed in Manage but only synced if pinned
            var allSeasons = seasons.Where(s => (s.IndexNumber ?? 0) >= 0).OrderBy(s => s.IndexNumber).ToList();
            if (allSeasons.Count == 0) continue;

            var mappings = allSeasons
                .Select(season => ResolveSeasonMapping(
                    userId, userCfg, cfg, seriesId, series, season,
                    Guid.TryParse(season.Id, out var sGuid) && episodeCounts.TryGetValue(sGuid, out var n) ? n : 0))
                .ToList();

            result.Add(new SeriesMapping
            {
                JellyfinSeriesId = seriesId,
                JellyfinSeriesName = series.Name ?? "",
                Seasons = mappings,
            });
        }

        return result;
    }

    /// <summary>
    /// Works out which MAL entry one Jellyfin season maps to, in priority order:
    /// a user override (pin or block), then the item's MyAnimeList provider ID,
    /// then this user's resolved-ID cache.
    /// </summary>
    private SeasonMapping ResolveSeasonMapping(
        string userId,
        Configuration.UserMalConfig userCfg,
        Configuration.PluginConfiguration cfg,
        string seriesId,
        JfItem series,
        JfItem season,
        int jellyfinEpisodes)
    {
        var seasonNum = season.IndexNumber ?? 0;

        var syncOverride = GetSyncOverride(userCfg, seriesId, seasonNum);
        var isPinned = syncOverride?.PinnedMalId != null;
        var isBlocked = syncOverride?.Blocked == true;

        string? malId = null;
        string? malIdSource = "none";
        string? malTitle = null;
        string? malImageUrl = null;
        var malEpisodes = 0;

        if (isBlocked)
        {
            malIdSource = "blocked";
        }
        else if (isPinned)
        {
            malId = syncOverride!.PinnedMalId;
            malTitle = syncOverride.PinnedMalTitle;
            malImageUrl = syncOverride.PinnedMalImageUrl;
            malIdSource = "pinned";
        }
        else
        {
            // Provider ID (most authoritative)
            malId = season.ProviderIds?.GetValueOrDefault("MyAnimeList")
                 ?? (seasonNum == 1 ? series.ProviderIds?.GetValueOrDefault("MyAnimeList") : null);
            if (malId is not null) malIdSource = "provider";

            // Cache. A miss recorded by a previous sync is meaningful — it is the
            // difference between "we looked and found nothing" and "never looked".
            if (malId is null)
            {
                var cached = GetCachedEntry(userId, NormalizeTitle(series.Name ?? ""), seasonNum, cfg.CacheTtlDays);
                if (cached is null)
                {
                    malIdSource = "unchecked";
                }
                else if (cached.NoMatch || string.IsNullOrEmpty(cached.MalId))
                {
                    malIdSource = "nomatch";
                }
                else
                {
                    malId = cached.MalId;
                    malTitle = cached.MalTitle;
                    malImageUrl = cached.MalImageUrl;
                    malEpisodes = cached.MalEpisodes;
                    malIdSource = "cache";
                }
            }
        }

        var rangeMap = userCfg.EpisodeRangeMappings
            .FirstOrDefault(m => m.JellyfinSeriesId == seriesId && m.SeasonNumber == seasonNum);

        // A split is the whole answer for a season: SyncUserAsync applies the ranges
        // and never looks at a single match. Report that, rather than a single match
        // that is not in use — the two are alternatives, not layers.
        if (!isBlocked && rangeMap is { Ranges.Count: > 0 })
            malIdSource = "split";

        return new SeasonMapping
        {
            SeasonNumber = seasonNum,
            MalId = malId,
            MalTitle = malTitle,
            MalImageUrl = malImageUrl,
            MalIdSource = malIdSource ?? "none",
            MalEpisodes = malEpisodes,
            JellyfinEpisodes = jellyfinEpisodes,
            SplitSuggested = rangeMap is null or { Ranges.Count: 0 }
                && userCfg.StaleRangeNotices.Any(n =>
                    n.JellyfinSeriesId == seriesId && n.SeasonNumber == seasonNum && n.Kind == "split"),
            Pinned = isPinned,
            Blocked = isBlocked,
            IsSpecial = seasonNum == 0,
            EpisodeRanges = rangeMap?.Ranges
                .Select(r => new EpisodeRangeInfo(r.Id, r.EpisodeFrom, r.EpisodeTo, r.MalId, r.MalTitle, r.MalImageUrl))
                .ToList(),
        };
    }

    /// <summary>
    /// Resolves the MAL mapping for a single Jellyfin series or season without
    /// walking the whole library. Used by the "open on MyAnimeList" lookup, which
    /// runs on every visit to an item page and must stay cheap.
    /// Accepts the ID of a series, a season or an episode.
    /// </summary>
    public SeriesMapping? GetSeriesMapping(string userId, Guid itemId)
    {
        EnsurePersistentCacheLoaded();

        var cfg = MalSyncPlugin.Instance!.Configuration;
        var userCfg = _auth.GetOrCreateUserConfig(userId);

        var jfUser = _userManager.GetUserById(Guid.Parse(userId));
        if (jfUser is null) return null;

        var item = _libraryManager.GetItemById(itemId);
        if (item is null) return null;

        // Walk up from an episode or season to the series it belongs to, so the
        // caller can pass whatever item the user happens to be looking at.
        int? focusSeason = null;
        while (item is not null && item is not MediaBrowser.Controller.Entities.TV.Series)
        {
            if (item is MediaBrowser.Controller.Entities.TV.Season s)
                focusSeason = s.IndexNumber ?? 0;
            else if (item is MediaBrowser.Controller.Entities.TV.Episode ep)
                focusSeason = ep.ParentIndexNumber;

            item = item.GetParent();
        }
        if (item is null) return null;

        var series = ToJfItem(item, jfUser);
        var seriesId = series.Id;

        var seasons = GetSeasons(item.Id, jfUser)
            .Where(x => (x.IndexNumber ?? 0) >= 0)
            .Where(x => focusSeason is null || (x.IndexNumber ?? 0) == focusSeason.Value)
            .OrderBy(x => x.IndexNumber)
            .ToList();

        return new SeriesMapping
        {
            JellyfinSeriesId = seriesId,
            JellyfinSeriesName = series.Name ?? "",
            Seasons = seasons
                .Select(season => ResolveSeasonMapping(
                    userId, userCfg, cfg, seriesId, series, season,
                    Guid.TryParse(season.Id, out var sGuid)
                        ? GetEpisodes(sGuid, jfUser).Count
                        : 0))
                .ToList(),
        };
    }

    // ═════════════════════════════════════════════════════════════════════
    // SHARED MATCH LOOKUP (for Jellyfin's own item pages)
    // ═════════════════════════════════════════════════════════════════════

    // Item pages have no user context, so a match may only be shown there when it is
    // the same for everyone. This maps "<normalised series>::<season>" to the agreed
    // MAL ID, or to null where users disagree. Rebuilt on demand and thrown away
    // whenever a match changes.
    private Dictionary<string, string?>? _sharedMatches;
    private readonly object _sharedMatchLock = new();

    private void InvalidateSharedMatches()
    {
        lock (_sharedMatchLock) _sharedMatches = null;
    }

    /// <summary>Drops the shared-match snapshot after a user changed an override.</summary>
    public void InvalidateSharedMatchesPublic() => InvalidateSharedMatches();

    /// <summary>
    /// The MyAnimeList ID for a series/season that every user agrees on, or null.
    /// <para>
    /// Matches are per user: one person may have corrected a season that another left
    /// on the automatic match. Jellyfin's item pages are shared, so a link is only
    /// offered where there is nothing to disagree about. Reads memory only — this runs
    /// while item pages are being built and must stay cheap.
    /// </para>
    /// </summary>
    public string? GetSharedMalId(string? seriesName, string? seriesId, int seasonNumber)
    {
        if (string.IsNullOrWhiteSpace(seriesName)) return null;

        var map = _sharedMatches;
        if (map is null)
        {
            lock (_sharedMatchLock)
            {
                map = _sharedMatches ??= BuildSharedMatches();
            }
        }

        var key = $"{NormalizeTitle(seriesName)}::{seasonNumber}";
        if (map.TryGetValue(key, out var id)) return id;

        // A series page stands in for its first season.
        if (seasonNumber == 0 && map.TryGetValue($"{NormalizeTitle(seriesName)}::1", out var first))
            return first;

        return null;
    }

    private Dictionary<string, string?> BuildSharedMatches()
    {
        EnsurePersistentCacheLoaded();

        // Candidate IDs per series+season; a key resolves only when they all agree.
        var candidates = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        void Offer(string key, string? malId)
        {
            if (string.IsNullOrEmpty(malId)) return;
            if (!candidates.TryGetValue(key, out var set))
                candidates[key] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(malId);
        }

        foreach (var (key, entry) in _persistentCache)
        {
            if (entry.NoMatch || string.IsNullOrEmpty(entry.MalId)) continue;
            // key is "<userScope>::<normalised series>::<season>"
            var firstSep = key.IndexOf("::", StringComparison.Ordinal);
            if (firstSep < 0) continue;
            Offer(key[(firstSep + 2)..], entry.MalId);
        }

        // A pin is a deliberate choice and outranks nothing here — it still only
        // counts as one more opinion, so a disagreement still hides the link.
        var cfg = MalSyncPlugin.Instance?.Configuration;
        if (cfg is not null)
        {
            foreach (var uc in cfg.UserConfigs)
            {
                foreach (var ov in uc.SeriesOverrides)
                {
                    if (ov.Blocked || string.IsNullOrEmpty(ov.PinnedMalId)) continue;
                    if (string.IsNullOrWhiteSpace(ov.JellyfinSeriesName)) continue;
                    Offer($"{NormalizeTitle(ov.JellyfinSeriesName)}::{ov.SeasonNumber}", ov.PinnedMalId);
                }
            }
        }

        return candidates.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count == 1 ? kv.Value.First() : null,
            StringComparer.Ordinal);
    }

    /// <summary>Searches MAL and returns results with images. Requires a valid MAL token for the user.</summary>
    public async Task<List<MalSearchResult>> SearchMalAsync(
        string query, string userId, int offset = 0, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(userId).ConfigureAwait(false);
        if (token is null) return new();

        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            var url = $"https://api.myanimelist.net/v2/anime?q={Uri.EscapeDataString(query)}" +
                      $"&limit=12&offset={offset}&fields=id,title,alternative_titles,main_picture,num_episodes,status,media_type,genres,start_season&nsfw=true";
            var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new();

            var doc = await resp.Content.ReadFromJsonAsync<MalSearchPage>(cancellationToken: ct).ConfigureAwait(false);
            var results = new List<MalSearchResult>();

            foreach (var entry in doc?.Data ?? Enumerable.Empty<MalSearchEntry>())
            {
                var node = entry.Node;
                var alt = node.AlternativeTitles ?? new();
                var seasonStr = node.StartSeason is not null
                    ? $"{Capitalize(node.StartSeason.Season)} {node.StartSeason.Year}"
                    : null;
                results.Add(new MalSearchResult
                {
                    MalId = node.Id.ToString(),
                    Title = node.Title ?? "",
                    EnglishTitle = alt.En,
                    Synonyms = alt.Synonyms?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList(),
                    ImageUrl = node.MainPicture?.Medium,
                    ImageUrlLarge = node.MainPicture?.Large,
                    NumEpisodes = node.NumEpisodes,
                    Status = node.Status ?? "",
                    MediaType = node.MediaType ?? "",
                    Genres = node.Genres?.Select(g => g.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).Take(5).ToList(),
                    StartSeason = seasonStr,
                });
            }
            return results;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAL search failed for query '{Query}'", query);
            return new();
        }
    }

    /// <summary>Fetches details for a single MAL anime entry (title, image, episodes, status).</summary>
    public async Task<MalSearchResult?> GetMalAnimeDetailsAsync(
        string malId, string userId, CancellationToken ct = default)
    {
        var token = await _auth.GetAccessTokenAsync(userId).ConfigureAwait(false);
        if (token is null) return null;

        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            var url = $"https://api.myanimelist.net/v2/anime/{malId}" +
                      "?fields=id,title,alternative_titles,main_picture,num_episodes,status,media_type,genres,start_season";
            var resp = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var node = await resp.Content.ReadFromJsonAsync<MalNode>(cancellationToken: ct).ConfigureAwait(false);
            if (node is null) return null;

            var alt = node.AlternativeTitles ?? new();
            var seasonStr = node.StartSeason is not null
                ? $"{Capitalize(node.StartSeason.Season)} {node.StartSeason.Year}"
                : null;
            var result = new MalSearchResult
            {
                MalId = node.Id.ToString(),
                Title = node.Title ?? "",
                EnglishTitle = alt.En,
                Synonyms = alt.Synonyms?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(3).ToList(),
                ImageUrl = node.MainPicture?.Medium,
                ImageUrlLarge = node.MainPicture?.Large,
                NumEpisodes = node.NumEpisodes,
                Status = node.Status ?? "",
                MediaType = node.MediaType ?? "",
                Genres = node.Genres?.Select(g => g.Name ?? "").Where(n => !string.IsNullOrEmpty(n)).Take(5).ToList(),
                StartSeason = seasonStr,
            };

            // Cache the image URL so the series list can show it without additional calls
            var cached = _malIdCache.Values.FirstOrDefault(e => e.MalId == malId)
                      ?? _persistentCache.Values.FirstOrDefault(e => e.MalId == malId);
            if (cached is not null)
            {
                foreach (var key in _malIdCache.Keys.Where(k => _malIdCache[k].MalId == malId).ToList())
                    _malIdCache[key] = _malIdCache[key] with { MalTitle = result.Title, MalImageUrl = result.ImageUrl };
                foreach (var key in _persistentCache.Keys.Where(k => _persistentCache[k].MalId == malId).ToList())
                    _persistentCache[key] = _persistentCache[key] with { MalTitle = result.Title, MalImageUrl = result.ImageUrl };
                _ = SavePersistentCacheAsync();
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "MAL anime details fetch failed for ID {Id}", malId);
            return null;
        }
    }

    /// <summary>Clears a single cache entry for one series/season.</summary>
    public void ClearCacheEntry(string userId, string seriesName, int seasonNumber)
    {
        if (seasonNumber < 0)
        {
            // Clear all seasons for this series
            var norm = NormalizeTitle(seriesName);
            var prefix = $"{userId}::{norm}::";
            foreach (var key in _malIdCache.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _malIdCache.Remove(key);
            foreach (var key in _persistentCache.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _persistentCache.Remove(key);
        }
        else
        {
            var key = $"{userId}::{NormalizeTitle(seriesName)}::{seasonNumber}";
            _malIdCache.Remove(key);
            _persistentCache.Remove(key);
        }
        _ = SavePersistentCacheAsync();
        InvalidateSharedMatches();
    }

    /// <summary>
    /// Walks the MAL sequel chain starting from <paramref name="startMalId"/> and builds
    /// cumulative episode-range suggestions for an absolute-numbered Jellyfin season.
    /// </summary>
    public async Task<List<EpisodeRangeInfo>> DetectEpisodeRangesAsync(
        string startMalId, string userId, CancellationToken ct = default,
        Guid? jellyfinSeriesId = null, int? seasonNumber = null, User? jfUser = null)
    {
        var token = await _auth.GetAccessTokenAsync(userId).ConfigureAwait(false);
        if (token is null) return new();

        // Collect chain entries: (MalId, Title, ImageUrl, NumEpisodes)
        var chain = new List<(string Id, string Title, string? ImageUrl, int NumEpisodes)>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { startMalId };
        var current = startMalId;

        for (var hop = 0; hop < 14; hop++)
        {
            try
            {
                using var http = _httpFactory.CreateClient("MalSync");
                http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

                var resp = await http.GetAsync(
                    $"https://api.myanimelist.net/v2/anime/{current}" +
                    "?fields=title,alternative_titles,num_episodes,main_picture,related_anime,media_type",
                    ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) break;

                var node = await resp.Content.ReadFromJsonAsync<MalNode>(cancellationToken: ct).ConfigureAwait(false);
                if (node is null) break;

                // Skip entries with suspiciously few episodes (individual recap/special entries
                // in the MAL chain that would create bogus 1-episode ranges).
                // Always include the start entry; filter subsequent entries.
                if (hop > 0 && node.NumEpisodes > 0 && node.NumEpisodes < 4)
                {
                    _logger.LogDebug("Range detection: skipping {Id} '{Title}' ({Eps} eps) — too few episodes for a season part.",
                        current, node.Title, node.NumEpisodes);
                    // Try to continue the chain past this entry
                    var skipSequel = node.RelatedAnime?
                        .Where(r => r.RelationType is "sequel" && !visited.Contains(r.Node.Id.ToString()))
                        .FirstOrDefault();
                    if (skipSequel is null) break;
                    var skipId = skipSequel.Node.Id.ToString();
                    visited.Add(skipId);
                    current = skipId;
                    continue;
                }

                var displayTitle = node.AlternativeTitles?.En ?? node.Title ?? current;
                chain.Add((current, displayTitle, node.MainPicture?.Medium, node.NumEpisodes));

                var nextId = FindNextPart(node, visited);
                if (nextId is null) break;

                visited.Add(nextId);
                current = nextId;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Range detection: chain error at {Id}", current);
                break;
            }
        }

        if (chain.Count == 0) return new();

        // MyAnimeList does not always link split cours as sequels — Vanitas no Carte
        // and Part 2 are a well-known example. When the relations lead nowhere but
        // the season clearly holds more than the first entry covers, look the later
        // parts up by name instead, which is how a person would find them.
        if (chain.Count == 1)
        {
            var extra = await FindLaterPartsByTitleAsync(
                chain[0].Title, chain[0].Id, visited, token, ct).ConfigureAwait(false);
            chain.AddRange(extra);
        }

        // Determine how many relative episodes the Jellyfin season actually has.
        // This lets us stop the chain early instead of mapping every sequel ever.
        int? maxSeasonEpisode = null;
        if (jellyfinSeriesId.HasValue && seasonNumber.HasValue && jfUser is not null)
        {
            var eps = GetEpisodesBySeriesAndSeason(jellyfinSeriesId.Value, seasonNumber.Value, jfUser);
            if (eps.Count > 0)
            {
                var minIdx = eps.Min(e => e.IndexNumber ?? 1);
                var offset = minIdx > 12 ? minIdx - 1 : 0;
                maxSeasonEpisode = eps.Max(e => (e.IndexNumber ?? 0) - offset);
            }
        }

        // Build cumulative ranges
        var ranges = new List<EpisodeRangeInfo>();
        var episodeStart = 1;
        for (var i = 0; i < chain.Count; i++)
        {
            // Stop once we've covered all episodes that actually exist in the Jellyfin season
            if (maxSeasonEpisode.HasValue && episodeStart > maxSeasonEpisode.Value)
                break;

            var (id, title, image, numEps) = chain[i];
            var isLast = i == chain.Count - 1;

            int episodeEnd;
            if (numEps > 0)
            {
                // If this entry would exceed the season's episode count, make it the last range
                if (maxSeasonEpisode.HasValue && episodeStart + numEps - 1 >= maxSeasonEpisode.Value)
                    isLast = true;

                episodeEnd = isLast ? 0 : episodeStart + numEps - 1;
            }
            else
            {
                // Unknown episode count → open-ended, stop after this
                episodeEnd = 0;
                isLast = true;
            }

            ranges.Add(new EpisodeRangeInfo(
                Guid.NewGuid().ToString("N")[..8],
                episodeStart,
                episodeEnd,
                id,
                title,
                image));

            if (numEps > 0)
                episodeStart += numEps;
            if (isLast) break;
        }

        return ranges;
    }

    /// <summary>
    /// The next part of a series within the same MyAnimeList chain.
    /// A plain sequel is the normal link; where that is missing, a related entry whose
    /// title is the same show plus a part marker ("Part 2", "2nd Season") is accepted
    /// regardless of how MyAnimeList classified the relation.
    /// </summary>
    private static string? FindNextPart(MalNode node, ISet<string> visited)
    {
        var related = node.RelatedAnime;
        if (related is null || related.Count == 0) return null;

        var sequel = related.FirstOrDefault(r =>
            r.RelationType is "sequel" && !visited.Contains(r.Node.Id.ToString()));
        if (sequel is not null) return sequel.Node.Id.ToString();

        var baseTitle = StripPartSuffix(node.AlternativeTitles?.En ?? node.Title ?? string.Empty);
        if (baseTitle.Length < 3) return null;

        foreach (var rel in related)
        {
            var id = rel.Node.Id.ToString();
            if (visited.Contains(id)) continue;
            if (rel.RelationType is "summary" or "character" or "other" or "spin_off") continue;

            var title = rel.Node.AlternativeTitles?.En ?? rel.Node.Title ?? string.Empty;
            if (LooksLikeLaterPartOf(baseTitle, title)) return id;
        }

        return null;
    }

    /// <summary>
    /// Searches MyAnimeList for later parts of <paramref name="firstTitle"/> and returns
    /// them in part order. Used when the relation graph does not connect them.
    /// </summary>
    private async Task<List<(string Id, string Title, string? ImageUrl, int NumEpisodes)>>
        FindLaterPartsByTitleAsync(
            string firstTitle, string firstId, ISet<string> visited, string token, CancellationToken ct)
    {
        var found = new List<(int Part, string Id, string Title, string? ImageUrl, int NumEpisodes)>();
        var baseTitle = StripPartSuffix(firstTitle);
        if (baseTitle.Length < 3) return new();

        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"Bearer {token}");

            var resp = await http.GetAsync(
                $"https://api.myanimelist.net/v2/anime?q={Uri.EscapeDataString(baseTitle)}&limit=15" +
                "&fields=id,title,alternative_titles,num_episodes,media_type,main_picture",
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return new();

            var doc = await resp.Content.ReadFromJsonAsync<MalSearchPage>(cancellationToken: ct).ConfigureAwait(false);
            foreach (var entry in doc?.Data ?? Enumerable.Empty<MalSearchEntry>())
            {
                var node = entry.Node;
                var id = node.Id.ToString();
                if (id == firstId || visited.Contains(id)) continue;

                // Recap and special entries would produce bogus one-episode ranges.
                if (node.MediaType is "music" or "special" or "ova") continue;
                if (node.NumEpisodes > 0 && node.NumEpisodes < 4) continue;

                var title = node.AlternativeTitles?.En ?? node.Title ?? string.Empty;
                var part = GetPartNumber(baseTitle, title);
                if (part < 2) continue;

                visited.Add(id);
                found.Add((part, id, title, node.MainPicture?.Medium, node.NumEpisodes));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Range detection: title search failed for '{Title}'", baseTitle);
            return new();
        }

        return found
            .OrderBy(f => f.Part)
            .Select(f => (f.Id, f.Title, f.ImageUrl, f.NumEpisodes))
            .ToList();
    }

    /// <summary>Removes a trailing part/season marker, e.g. "Vanitas no Carte Part 2" → "Vanitas no Carte".</summary>
    private static string StripPartSuffix(string title)
    {
        var t = Regex.Replace(title, @"\s*[:\-–—]?\s*\b(?:part|cour|season)\s*\d+\s*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+\d+(?:st|nd|rd|th)\s+season\s*$", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"\s+(?:II|III|IV|V)\s*$", "", RegexOptions.None);
        return t.Trim();
    }

    /// <summary>
    /// Which part of <paramref name="baseTitle"/> a title represents, or 0 when it is
    /// not the same show. "Vanitas no Carte Part 2" against "Vanitas no Carte" gives 2.
    /// </summary>
    private static int GetPartNumber(string baseTitle, string title)
    {
        if (!LooksLikeSameShow(baseTitle, title)) return 0;

        var t = NormalizeForMatch(title);
        var m = Regex.Match(t, @"\b(?:part|cour|season)\s*(\d+)\b", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n)) return n;

        m = Regex.Match(t, @"\b(\d+)(?:st|nd|rd|th)\s+season\b", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out n)) return n;

        if (Regex.IsMatch(t, @"\biii\b")) return 3;
        if (Regex.IsMatch(t, @"\bii\b")) return 2;

        return 0;
    }

    private static bool LooksLikeSameShow(string baseTitle, string title)
        => TitleScore(baseTitle, StripPartSuffix(title)) >= 0.85;

    private static bool LooksLikeLaterPartOf(string baseTitle, string title)
        => GetPartNumber(baseTitle, title) >= 2;

    /// <summary>Sends a Discord-compatible webhook notification.</summary>
    public async Task SendWebhookAsync(string webhookUrl, string title, string description, CancellationToken ct = default)
    {
        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            var payload = new
            {
                username = "MAL Sync",
                embeds = new[]
                {
                    new
                    {
                        title,
                        description,
                        color = 16744448, // orange #FF6E00
                        footer = new { text = "MAL Sync • Jellyfin" },
                    }
                }
            };
            var json = System.Text.Json.JsonSerializer.Serialize(payload);
            await http.PostAsync(webhookUrl,
                new StringContent(json, System.Text.Encoding.UTF8, "application/json"), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send webhook notification to {Url}", webhookUrl);
        }
    }

    /// <summary>Clears the MAL-ID cache. Pass null to clear all users, or a userId to clear only that user.</summary>
    public void ClearCache(string? userId = null)
    {
        if (userId is null)
        {
            _malIdCache.Clear();
            _persistentCache.Clear();
        }
        else
        {
            var prefix = $"{userId}::";
            foreach (var key in _malIdCache.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _malIdCache.Remove(key);
            foreach (var key in _persistentCache.Keys.Where(k => k.StartsWith(prefix)).ToList())
                _persistentCache.Remove(key);
        }
        _ = SavePersistentCacheAsync();
        InvalidateSharedMatches();
    }

    // ═════════════════════════════════════════════════════════════════════
    // OVERRIDE HELPERS
    // ═════════════════════════════════════════════════════════════════════

    private static Configuration.SeriesOverride? GetSyncOverride(
        Configuration.UserMalConfig userCfg, string seriesId, int seasonNum)
    {
        // Season-specific override takes priority over all-seasons (SeasonNumber == 0)
        return userCfg.SeriesOverrides.FirstOrDefault(
                o => o.JellyfinSeriesId == seriesId && o.SeasonNumber == seasonNum)
            ?? userCfg.SeriesOverrides.FirstOrDefault(
                o => o.JellyfinSeriesId == seriesId && o.SeasonNumber == 0);
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

    /// <summary>
    /// Episode counts for every season the user can see, keyed by season ID.
    /// Episodes normally hang off a Season; a flat library puts them directly under
    /// the Series, so both parents are recorded and the caller looks up whichever
    /// applies.
    /// </summary>
    private Dictionary<Guid, int> GetEpisodeCountsBySeason(User user)
    {
        var counts = new Dictionary<Guid, int>();
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            Recursive = true,
            IsMissing = false,
        });

        foreach (var item in items)
        {
            var parent = item is MediaBrowser.Controller.Entities.TV.Episode ep && ep.SeasonId != Guid.Empty
                ? ep.SeasonId
                : item.ParentId;
            if (parent == Guid.Empty) continue;
            counts[parent] = counts.TryGetValue(parent, out var n) ? n + 1 : 1;
        }

        return counts;
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

    /// <summary>
    /// Fallback for libraries where episodes live directly under the series
    /// without an intermediate Season folder.
    /// </summary>
    private List<JfItem> GetEpisodesBySeriesAndSeason(Guid seriesId, int seasonNumber, User user)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            IncludeItemTypes = [BaseItemKind.Episode],
            AncestorIds = [seriesId],
            IsMissing = false,
        });
        return items
            .Where(i => (i.ParentIndexNumber ?? 1) == seasonNumber)
            .Select(i => ToJfItem(i, user))
            .ToList();
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
            ProductionYear = item.ProductionYear ?? item.PremiereDate?.Year,
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
            @params = string.Empty;
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
                $"https://api.myanimelist.net/v2/anime/{malId}?fields=num_episodes,status,title,alternative_titles,main_picture",
                ct).ConfigureAwait(false);
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
        int maxHops = 14)
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

                // When multiple sequels exist (e.g. split cour + special), prefer the one
                // whose title already contains the target season number instead of blindly
                // picking the first entry.  This fixes Re:Zero S4, Dr. Stone Part 3, etc.
                var best = sequels.Count == 1
                    ? sequels[0]
                    : (sequels.FirstOrDefault(s => ContainsSeasonNumber(s.Node.Title ?? "", targetSeason))
                       ?? sequels[0]);

                var node = best.Node;
                var nid = node.Id.ToString();
                visited.Add(nid);
                chain.Add((nid, node.Title ?? ""));
                current = nid;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Sequel chain error at ID {Id}", current); break; }
        }

        if (chain.Count == 0) return null;

        var baseTitle = StripSeasonSuffix(seriesName);

        // 1. Season-number match by title
        foreach (var (cid, ctitle) in chain)
            if (ContainsSeasonNumber(ctitle, targetSeason)
                && TitleSimilarity(baseTitle, StripSeasonSuffix(ctitle)) >= 0.4)
                return cid;

        // 2. Index fallback (S2 → chain[0], S3 → chain[1], …)
        var pos = targetSeason - 2;
        if (pos >= 0 && pos < chain.Count) return chain[pos].Id;

        // 3. Last entry
        return chain[^1].Id;
    }

    /// <summary>
    /// Extra evidence about the Jellyfin side of a match. Titles alone are often
    /// ambiguous — remakes share a name, and MAL search happily returns music videos
    /// and specials — so episode count and year are used to separate candidates that
    /// score alike on the title.
    /// </summary>
    /// <param name="EpisodeCount">Episodes present in the Jellyfin season, 0 when unknown.</param>
    /// <param name="Year">Jellyfin's production/premiere year, null when unknown.</param>
    private readonly record struct MatchHints(int EpisodeCount, int? Year)
    {
        public static readonly MatchHints None = new(0, null);
    }

    /// <summary>
    /// Scores one MyAnimeList search hit against the series being matched.
    /// The title carries the decision; the other signals only nudge it, so a clearly
    /// better title still wins over a coincidental episode-count agreement.
    /// </summary>
    private static double ScoreCandidate(
        MalNode node, string query, int seasonNum, MatchHints hints, out bool isSequelCandidate)
    {
        var alt = node.AlternativeTitles ?? new();
        var titles = new List<string> { node.Title ?? "" };
        if (!string.IsNullOrEmpty(alt.En)) titles.Add(alt.En);
        if (alt.Synonyms is not null) titles.AddRange(alt.Synonyms);
        titles.RemoveAll(string.IsNullOrWhiteSpace);
        if (titles.Count == 0) { isSequelCandidate = false; return 0.0; }

        var allTitles = string.Join(" ", titles);
        isSequelCandidate = IsSequelTitle(allTitles);

        var baseQuery = StripSeasonSuffix(query);
        var score = titles.Max(t => TitleScore(query, t));

        if (seasonNum <= 1)
        {
            // A first season must match the bare title, not a sequel's.
            var baseScore = titles.Select(StripSeasonSuffix).Max(t => TitleScore(baseQuery, t));
            score = Math.Min(score, baseScore);

            // Guard against a shared first word carrying an unrelated franchise entry.
            var qFirst = MatchTokens(baseQuery).FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrEmpty(qFirst))
            {
                var firstScore = titles
                    .Select(t => MatchTokens(StripSeasonSuffix(t)).FirstOrDefault() ?? string.Empty)
                    .Select(w => Similarity(qFirst, w))
                    .DefaultIfEmpty(0).Max();
                if (firstScore < 0.5) score *= 0.15;
            }

            if (isSequelCandidate) score *= 0.12;
        }
        else
        {
            var bases = titles.Select(StripSeasonSuffix).ToList();
            var baseScore = bases.Max(t => TitleScore(baseQuery, t));
            if (!ContainsSeasonNumber(allTitles, seasonNum)) baseScore *= 0.4;

            var qFirst = MatchTokens(baseQuery).FirstOrDefault() ?? string.Empty;
            if (!string.IsNullOrEmpty(qFirst))
            {
                var maxFirst = bases
                    .Select(t => MatchTokens(t).FirstOrDefault() ?? string.Empty)
                    .Select(w => Similarity(qFirst, w))
                    .DefaultIfEmpty(0).Max();
                if (maxFirst < 0.5) baseScore *= 0.15;
            }
            score = Math.Min(score, baseScore);
        }

        if (score <= 0) return 0.0;

        // ── Media type ────────────────────────────────────────────────────
        // MAL search mixes openings, specials and films into results for a TV title.
        score *= (node.MediaType ?? string.Empty).ToLowerInvariant() switch
        {
            "tv" or "ona" or "" => 1.0,
            "ova" or "special" => 0.80,
            "movie" => hints.EpisodeCount > 1 ? 0.55 : 0.90,
            "music" => 0.20,
            _ => 0.95,
        };

        // ── Episode count ─────────────────────────────────────────────────
        // MAL reports 0 while a show is still airing, and a Jellyfin season is often
        // incomplete, so only a Jellyfin season that is *longer* than the MAL entry is
        // evidence against the match — and even then only mildly, because that is also
        // exactly what a multi-cour season looks like.
        if (hints.EpisodeCount > 0 && node.NumEpisodes > 0)
        {
            var diff = Math.Abs(node.NumEpisodes - hints.EpisodeCount);
            if (diff == 0) score += 0.10;
            else if (diff <= 2) score += 0.05;
            else if (hints.EpisodeCount > node.NumEpisodes * 2) score -= 0.06;
        }

        // ── Year ──────────────────────────────────────────────────────────
        // The reliable way to tell a remake from the original it is named after.
        if (hints.Year is int jfYear && node.StartSeason?.Year is int malYear && malYear > 1900)
        {
            var gap = Math.Abs(malYear - jfYear);
            if (gap == 0) score += 0.08;
            else if (gap == 1) score += 0.03;
            else if (gap >= 4) score -= 0.12;
        }

        return Math.Clamp(score, 0.0, 1.0);
    }

    /// <summary>A MAL entry a search settled on, with enough detail to cache it usefully.</summary>
    private sealed record MalMatch(string Id, string? Title, string? ImageUrl, int Episodes);

    private async Task<string?> SearchMalIdAsync(
        string title, Dictionary<string, string> headers, int seasonNum,
        double minSimilarity, CancellationToken ct,
        ISet<string>? excludedIds = null,
        MatchHints hints = default)
        => (await SearchMalMatchAsync(title, headers, seasonNum, minSimilarity, ct, excludedIds, hints)
                .ConfigureAwait(false))?.Id;

    private async Task<MalMatch?> SearchMalMatchAsync(
        string title, Dictionary<string, string> headers, int seasonNum,
        double minSimilarity, CancellationToken ct,
        ISet<string>? excludedIds = null,
        MatchHints hints = default)
    {
        try
        {
            using var http = _httpFactory.CreateClient("MalSync");
            foreach (var (k, v) in headers) http.DefaultRequestHeaders.TryAddWithoutValidation(k, v);

            // A wider net than before, and with the fields needed to judge a hit:
            // the right entry regularly sat outside the old top five.
            var resp = await http.GetAsync(
                $"https://api.myanimelist.net/v2/anime?q={Uri.EscapeDataString(title)}&limit=15" +
                "&fields=id,title,alternative_titles,num_episodes,media_type,status,start_season,main_picture",
                ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;

            var doc = await resp.Content.ReadFromJsonAsync<MalSearchPage>(cancellationToken: ct).ConfigureAwait(false);

            MalNode? best = null;
            double bestScore = 0;
            MalNode? bestNonSequel = null;
            double bestNonSequelScore = 0;

            foreach (var entry in doc?.Data ?? Enumerable.Empty<MalSearchEntry>())
            {
                var node = entry.Node;
                if (excludedIds is not null && excludedIds.Contains(node.Id.ToString())) continue;

                var score = ScoreCandidate(node, title, seasonNum, hints, out var isSequelCandidate);

                if (score > bestScore)
                {
                    bestScore = score;
                    best = node;
                }

                if (seasonNum == 1 && !isSequelCandidate && score > bestNonSequelScore)
                {
                    bestNonSequelScore = score;
                    bestNonSequel = node;
                }
            }

            if (seasonNum == 1)
                return bestNonSequelScore >= minSimilarity ? ToMatch(bestNonSequel) : null;

            if (bestScore >= minSimilarity) return ToMatch(best);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "MAL search failed for '{Title}'", title); }
        return null;

        static MalMatch? ToMatch(MalNode? node) => node is null ? null : new MalMatch(
            node.Id.ToString(),
            node.AlternativeTitles?.En is { Length: > 0 } en ? en : node.Title,
            node.MainPicture?.Medium ?? node.MainPicture?.Large,
            node.NumEpisodes);
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
            var qFirst = MatchTokens(baseQ).FirstOrDefault() ?? string.Empty;
            foreach (var (norm, mid, _) in entries)
            {
                if (excludedIds is not null && excludedIds.Contains(mid))
                    continue;

                var isSequelCandidate = IsSequelTitle(norm);
                var score = TitleScore(normQ, norm);
                var baseT = NormalizeTitle(StripSeasonSuffix(norm));
                score = Math.Min(score, TitleScore(baseQ, baseT));

                if (!string.IsNullOrEmpty(qFirst))
                {
                    var tFirst = MatchTokens(baseT).FirstOrDefault() ?? string.Empty;
                    if (Similarity(qFirst, tFirst) < 0.5) score *= 0.15;
                }

                if (isSequelCandidate) score *= 0.12;
                if (score > bestScore) { bestScore = score; bestId = mid; }

                if (!isSequelCandidate && score > bestNonSequelScore)
                {
                    bestNonSequelScore = score;
                    bestNonSequelId = mid;
                }
            }

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
                var score = TitleScore(baseQ, baseT);
                if (!ContainsSeasonNumber(orig, seasonNum)) score *= 0.4;

                var qParts = MatchTokens(baseQ);
                var tParts = MatchTokens(baseT);
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
        if (malUserList.TryGetValue(malId, out var listEntry)
            && !string.IsNullOrWhiteSpace(listEntry.Title)
            && IsSequelTitle(listEntry.Title))
            return true;

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
    // MAL-ID CACHE (in-memory + persistent JSON)
    // ═════════════════════════════════════════════════════════════════════

    private void EnsurePersistentCacheLoaded()
    {
        if (_persistentCacheLoaded) return;
        _persistentCacheLoaded = true;

        try
        {
            if (!File.Exists(_cacheFilePath)) return;
            var json = File.ReadAllText(_cacheFilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(json, _jsonOpts);
            if (dict is null) return;
            foreach (var (k, v) in dict) _persistentCache[k] = v;
            _logger.LogDebug("Loaded {Count} MAL-ID cache entries from disk", _persistentCache.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load MAL-ID cache from disk");
        }
    }

    private async Task SavePersistentCacheAsync()
    {
        if (!await _cacheSaveLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var dir = Path.GetDirectoryName(_cacheFilePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_persistentCache, _jsonOpts);
            await File.WriteAllTextAsync(_cacheFilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to save MAL-ID cache to disk");
        }
        finally
        {
            _cacheSaveLock.Release();
        }
    }

    private string? GetCachedMalId(string userScope, string series, int season, int ttlDays)
    {
        var entry = GetCachedEntry(userScope, series, season, ttlDays);
        if (entry is null || entry.NoMatch || string.IsNullOrEmpty(entry.MalId)) return null;
        return entry.MalId;
    }

    private CacheEntry? GetCachedEntry(string userScope, string series, int season, int ttlDays)
    {
        var key = $"{userScope}::{series}::{season}";

        if (_malIdCache.TryGetValue(key, out var entry))
        {
            if ((DateTime.UtcNow - entry.CachedAt).TotalDays > ttlDays)
            {
                _malIdCache.Remove(key);
                _persistentCache.Remove(key);
                return null;
            }
            return entry;
        }

        if (_persistentCache.TryGetValue(key, out var persisted))
        {
            if ((DateTime.UtcNow - persisted.CachedAt).TotalDays > ttlDays)
            {
                _persistentCache.Remove(key);
                return null;
            }
            _malIdCache[key] = persisted; // promote to in-memory
            return persisted;
        }

        return null;
    }

    private void SetCachedMalId(string userScope, string series, int season, string malId,
        string? malTitle = null, string? malImageUrl = null, int malEpisodes = 0)
    {
        var key = $"{userScope}::{series}::{season}";
        var entry = new CacheEntry(malId, DateTime.UtcNow)
        {
            MalTitle = malTitle,
            MalImageUrl = malImageUrl,
            MalEpisodes = malEpisodes,
        };
        _malIdCache[key] = entry;
        _persistentCache[key] = entry;
        _ = SavePersistentCacheAsync();
        InvalidateSharedMatches();
    }

    /// <summary>
    /// Records that a search for this season found nothing, so the UI can tell a real
    /// miss apart from a season that has simply never been synced.
    /// </summary>
    private void SetCachedNoMatch(string userScope, string series, int season)
    {
        var key = $"{userScope}::{series}::{season}";
        var entry = new CacheEntry(string.Empty, DateTime.UtcNow) { NoMatch = true };
        _malIdCache[key] = entry;
        _persistentCache[key] = entry;
        _ = SavePersistentCacheAsync();
        InvalidateSharedMatches();
    }

    /// <summary>
    /// Keeps an existing match but fills in details the entry is missing. Matches
    /// resolved through the sequel chain, or from an ID the user's list does not
    /// cover, arrive without a title — which is why the library view used to show a
    /// bare "MAL 59978". This backfills them on the next sync.
    /// </summary>
    private void UpdateCachedDetails(
        string userScope, string series, int season,
        int malEpisodes = 0, string? malTitle = null, string? malImageUrl = null)
    {
        var key = $"{userScope}::{series}::{season}";
        if (!_persistentCache.TryGetValue(key, out var entry) || entry.NoMatch) return;

        var updated = entry with
        {
            MalEpisodes = malEpisodes > 0 ? malEpisodes : entry.MalEpisodes,
            MalTitle = string.IsNullOrWhiteSpace(entry.MalTitle) ? malTitle : entry.MalTitle,
            MalImageUrl = string.IsNullOrWhiteSpace(entry.MalImageUrl) ? malImageUrl : entry.MalImageUrl,
        };
        if (updated == entry) return;

        _malIdCache[key] = updated;
        _persistentCache[key] = updated;
        _ = SavePersistentCacheAsync();
        InvalidateSharedMatches();
    }

    // ═════════════════════════════════════════════════════════════════════
    // STRING / TITLE HELPERS  (mirrors the Python script)
    // ═════════════════════════════════════════════════════════════════════

    private static string Capitalize(string? s)
        => string.IsNullOrEmpty(s) ? "" : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();

    private static string NormalizeTitle(string t)
    {
        foreach (var (from, to) in UnicodeMap) t = t.Replace(from, to);
        return Regex.Replace(t.ToLowerInvariant().Trim(), @"\s+", " ");
    }

    /// <summary>
    /// Normalises a title for *comparison only*. Punctuation and bracket noise are
    /// dropped so "【Oshi no Ko】" and "Oshi no Ko" compare as equal.
    /// <para>
    /// Deliberately separate from <see cref="NormalizeTitle"/>, which is also used to
    /// build cache keys and must keep producing the same strings as before.
    /// </para>
    /// </summary>
    private static string NormalizeForMatch(string t)
    {
        t = NormalizeTitle(t);
        t = Regex.Replace(t, @"[\p{P}\p{S}]+", " ");
        return Regex.Replace(t, @"\s+", " ").Trim();
    }

    private static string[] MatchTokens(string t)
        => NormalizeForMatch(t).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when <paramref name="prefix"/> is the leading run of tokens of <paramref name="full"/>.</summary>
    private static bool IsTokenPrefix(string[] prefix, string[] full)
    {
        if (prefix.Length == 0 || prefix.Length > full.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (!string.Equals(prefix[i], full[i], StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>
    /// How well two titles match, on 0–1.
    /// <para>
    /// Edit distance alone reads badly on anime titles: MyAnimeList routinely appends a
    /// long subtitle ("Honzuki no Gekokujou: Shisho ni Naru Tame ni wa …"), which drags a
    /// perfectly good match down to noise. So the edit-distance ratio is combined with two
    /// structural signals — one title being the leading part of the other, and shared word
    /// sets — and the strongest signal wins.
    /// </para>
    /// </summary>
    private static double TitleScore(string query, string candidate)
    {
        var q = NormalizeForMatch(query);
        var c = NormalizeForMatch(candidate);
        if (q.Length == 0 || c.Length == 0) return 0.0;
        if (q == c) return 1.0;

        var best = Similarity(q, c);

        var qt = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ct = c.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // One title being the leading part of the other is strong evidence — but a
        // short stub must not claim a much longer title ("Attack" is not "Attack on
        // Titan"), so the credit needs substance first and then scales with how much
        // of the longer title the shorter one actually covers.
        if ((qt.Length >= 2 || q.Length >= 8)
            && (IsTokenPrefix(qt, ct) || IsTokenPrefix(ct, qt)))
        {
            var coverage = Math.Min(qt.Length, ct.Length) / (double)Math.Max(qt.Length, ct.Length);
            best = Math.Max(best, 0.60 + 0.32 * coverage);
        }

        // Same words, different order or with extras in between.
        var qs = new HashSet<string>(qt, StringComparer.Ordinal);
        var cs = new HashSet<string>(ct, StringComparer.Ordinal);
        if (qs.Count > 0 && cs.Count > 0)
        {
            var shared = qs.Count(t => cs.Contains(t));
            var jaccard = shared / (double)(qs.Count + cs.Count - shared);
            best = Math.Max(best, jaccard * 0.85);
        }

        return best;
    }

    private static double TitleSimilarity(string a, string b)
        => Similarity(NormalizeTitle(a), NormalizeTitle(b));

    private static double Similarity(string a, string b)
    {
        if (a == b) return 1.0;
        if (a.Length == 0 || b.Length == 0) return 0.0;
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

        if (Regex.IsMatch(t, @"\s+[2-9]\s*$", RegexOptions.IgnoreCase)) return true;

        return false;
    }

    private static bool ContainsSeasonNumber(string text, int n)
    {
        text = NormalizeTitle(text);

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

        if (n == 2 && Regex.IsMatch(text, @"\bii\b|\bni\s*!?\s*$", RegexOptions.IgnoreCase)) return true;
        if (n == 3 && Regex.IsMatch(text, @"\biii\b", RegexOptions.IgnoreCase)) return true;
        if (n == 4 && Regex.IsMatch(text, @"\biv\b|\byon\s*!?\s*$|\bshi\s*!?\s*$", RegexOptions.IgnoreCase)) return true;
        if (n == 5 && Regex.IsMatch(text, @"\bv\b|\bgo\s*!?\s*$", RegexOptions.IgnoreCase)) return true;

        return false;
    }

    // ═════════════════════════════════════════════════════════════════════
    // PUBLIC DATA MODELS (returned by management methods)
    // ═════════════════════════════════════════════════════════════════════

    public sealed class SeriesMapping
    {
        public string JellyfinSeriesId { get; set; } = string.Empty;
        public string JellyfinSeriesName { get; set; } = string.Empty;
        public List<SeasonMapping> Seasons { get; set; } = new();
    }

    public sealed class SeasonMapping
    {
        public int SeasonNumber { get; set; }
        public string? MalId { get; set; }
        public string? MalTitle { get; set; }
        public string? MalImageUrl { get; set; }

        /// <summary>
        /// Where the match came from: <c>split</c> (mapped to several MAL entries by
        /// episode range, which replaces a single match), <c>pinned</c>, <c>provider</c>,
        /// <c>cache</c>, <c>blocked</c>, <c>nomatch</c> (a sync searched and found
        /// nothing) or <c>unchecked</c> (this season has not been resolved yet).
        /// </summary>
        public string MalIdSource { get; set; } = "none";

        /// <summary>Episode count of the matched MAL entry, 0 when unknown.</summary>
        public int MalEpisodes { get; set; }

        /// <summary>Episodes actually present in this Jellyfin season.</summary>
        public int JellyfinEpisodes { get; set; }

        /// <summary>
        /// True when a sync found this season holds far more episodes than its MAL
        /// entry, i.e. it probably spans several MAL entries and wants a split.
        /// </summary>
        public bool SplitSuggested { get; set; }
        public bool Pinned { get; set; }
        public bool Blocked { get; set; }
        public bool IsSpecial { get; set; }
        public List<EpisodeRangeInfo>? EpisodeRanges { get; set; }
    }

    public sealed record EpisodeRangeInfo(
        string Id,
        int EpisodeFrom,
        int EpisodeTo,
        string MalId,
        string? MalTitle,
        string? MalImageUrl);

    public sealed class MalSearchResult
    {
        public string MalId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string? EnglishTitle { get; set; }
        public List<string>? Synonyms { get; set; }
        public string? ImageUrl { get; set; }
        public string? ImageUrlLarge { get; set; }
        public int NumEpisodes { get; set; }
        public string Status { get; set; } = string.Empty;
        public string MediaType { get; set; } = string.Empty;
        public List<string>? Genres { get; set; }
        public string? StartSeason { get; set; }
    }

    // ═════════════════════════════════════════════════════════════════════
    // LOCAL RECORD TYPES (JSON DTOs)
    // ═════════════════════════════════════════════════════════════════════

    private sealed record CacheEntry(string MalId, DateTime CachedAt)
    {
        [JsonPropertyName("malTitle")]
        public string? MalTitle { get; init; }
        [JsonPropertyName("malImageUrl")]
        public string? MalImageUrl { get; init; }

        /// <summary>
        /// Set when a sync searched for this season and came back empty. Purely so the
        /// UI can say "nothing found" instead of "not looked at yet" — the sync itself
        /// ignores these and retries every run, so a matching improvement takes effect
        /// immediately rather than after the cache expires.
        /// </summary>
        [JsonPropertyName("noMatch")]
        public bool NoMatch { get; init; }

        /// <summary>Episode count of the matched MAL entry, 0 when unknown.</summary>
        [JsonPropertyName("malEpisodes")]
        public int MalEpisodes { get; init; }
    }

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
        [JsonPropertyName("ProductionYear")] public int? ProductionYear { get; set; }
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
        [JsonPropertyName("media_type")] public string? MediaType { get; set; }
        [JsonPropertyName("alternative_titles")] public MalAltTitles? AlternativeTitles { get; set; }
        [JsonPropertyName("main_picture")] public MalPicture? MainPicture { get; set; }
        [JsonPropertyName("genres")] public List<MalGenre>? Genres { get; set; }
        [JsonPropertyName("start_season")] public MalStartSeason? StartSeason { get; set; }
        [JsonPropertyName("related_anime")] public List<MalRelatedEntry>? RelatedAnime { get; set; }
    }
    private sealed class MalAltTitles
    {
        [JsonPropertyName("en")] public string? En { get; set; }
        [JsonPropertyName("synonyms")] public List<string>? Synonyms { get; set; }
    }
    private sealed class MalPicture
    {
        [JsonPropertyName("medium")] public string? Medium { get; set; }
        [JsonPropertyName("large")] public string? Large { get; set; }
    }
    private sealed class MalGenre
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
    private sealed class MalStartSeason
    {
        [JsonPropertyName("year")] public int Year { get; set; }
        [JsonPropertyName("season")] public string? Season { get; set; }
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
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("alternative_titles")] public MalAltTitles? AlternativeTitles { get; set; }
        [JsonPropertyName("main_picture")] public MalPicture? MainPicture { get; set; }
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
