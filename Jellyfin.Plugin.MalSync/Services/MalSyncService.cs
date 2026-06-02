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
                                var msg = $"**{notice.JellyfinSeriesName}** — Staffel {notice.SeasonNumber}\n" +
                                          $"Mehr Folgen vorhanden als **{notice.MalTitle}** auf MAL hat.\n" +
                                          $"Ein neuer Part könnte verfügbar sein.\n\n" +
                                          $"*Manage → Edit → Auto-detect from MAL*";
                                _ = SendWebhookAsync(userCfg.WebhookUrl,
                                    "⚠️ MAL Sync: Ranges möglicherweise veraltet", msg);
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
                        if (malId2 is null)
                        {
                            // Primary search: full series name
                            Dbg($"No MAL ID for '{seriesName}' S{seasonNum}, searching by title…");
                            malId2 = await SearchMalIdAsync(seriesName, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                        }

                        // Fallback 1: strip subtitle after ":"
                        if (malId2 is null && seriesName.Contains(':'))
                        {
                            var noSubtitle = seriesName[..seriesName.IndexOf(':')].Trim();
                            if (noSubtitle.Length >= 3)
                            {
                                Dbg($"  Fallback search without subtitle: '{noSubtitle}'…");
                                malId2 = await SearchMalIdAsync(noSubtitle, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        // Fallback 2: strip trailing season/part suffix
                        if (malId2 is null)
                        {
                            var stripped = StripSeasonSuffix(seriesName);
                            if (stripped.Length >= 3 && stripped != seriesName)
                            {
                                Dbg($"  Fallback search stripped suffix: '{stripped}'…");
                                malId2 = await SearchMalIdAsync(stripped, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        if (malId2 is not null)
                        {
                            if (seasonNum == 1) s1IdCache.TryAdd(seriesId, malId2);
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId2,
                                malUserList.TryGetValue(malId2, out var uEntry2) ? uEntry2.Title : null);
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
                            malId2 = await GetMalSequelFromChainAsync(s1Id, seasonNum, seriesName, malHeaders, cancellationToken).ConfigureAwait(false);
                        }
                        if (malId2 is null)
                        {
                            var suffix = seasonNum switch { 2 => "2nd Season", 3 => "3rd Season", 4 => "4th Season", 5 => "5th Season", _ => $"{seasonNum}th Season" };
                            Dbg($"Sequel chain failed, direct search for '{seriesName} {suffix}'…");
                            malId2 = await SearchMalIdAsync($"{seriesName} {suffix}", malHeaders, seasonNum, cfg.MalSearchMinSimilarity, cancellationToken).ConfigureAwait(false);
                        }
                        if (malId2 is not null)
                        {
                            SetCachedMalId(cacheScope, normalizedSeriesName, seasonNum, malId2,
                                malUserList.TryGetValue(malId2, out var uEntry3) ? uEntry3.Title : null);
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
                            remapped = await SearchMalIdAsync(seriesName, malHeaders, 1, cfg.MalSearchMinSimilarity, cancellationToken, excluded).ConfigureAwait(false);
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
                // Users should trigger auto-detect manually via Manage → Edit → Episode Ranges → 🔍 Auto-detect.
                if (malUserList.TryGetValue(malId2, out var checkListEntry) && checkListEntry.Total > 0)
                {
                    var jfEpCount = GetEpisodes(Guid.Parse(seasonId), jfUser).Count;
                    if (jfEpCount == 0)
                        jfEpCount = GetEpisodesBySeriesAndSeason(Guid.Parse(seriesId), seasonNum, jfUser).Count;

                    if (jfEpCount > checkListEntry.Total * 2)
                    {
                        var label = seasonNum == 0 ? $"{seriesName} [Specials]" : $"{seriesName} S{seasonNum}";
                        Log($"[WARN] '{label}': Jellyfin has {jfEpCount} episodes but MAL entry only has {checkListEntry.Total}. " +
                            "If multiple MAL parts share one Jellyfin season, use Manage → Edit → Episode Ranges → 🔍 Auto-detect.");
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
            Log($"[WARN] {unresolved.Count} season(s) could not be matched to MAL — use the Manage tab to pin them manually:");
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
                    var desc = $"**{errors.Count} Fehler** beim Sync erkannt:\n" +
                               string.Join("\n", errors.Take(5).Select(l => $"• `{l}`"));
                    if (errors.Count > 5) desc += $"\n_{errors.Count - 5} weitere Fehler_";
                    _ = SendWebhookAsync(userCfg.WebhookUrl, "❌ MAL Sync: Fehler aufgetreten", desc);
                }
            }

            if (userCfg.WebhookOnSyncSummary && !dryRun)
            {
                var updates = log.Where(l => l.StartsWith("[MAL] ")).ToList();
                if (updates.Count > 0)
                {
                    var desc = $"**{updates.Count} Einträge** aktualisiert:\n" +
                               string.Join("\n", updates.Take(10).Select(l => $"• {l.Replace("[MAL] ", "")}"));
                    if (updates.Count > 10) desc += $"\n_{updates.Count - 10} weitere_";
                    _ = SendWebhookAsync(userCfg.WebhookUrl, "✅ MAL Sync abgeschlossen", desc);
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
                    $"A new sequel part may be available — re-run Auto-detect from MAL in the Manage tab to update ranges.");
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

        if (malEntry is null)
        {
            var info = await GetMalAnimeInfoAsync(malId, malHeaders, cancellationToken).ConfigureAwait(false);
            malTotal = info.NumEpisodes;
            airingStatus = info.Status ?? string.Empty;
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

            var mappings = new List<SeasonMapping>();
            foreach (var season in allSeasons)
            {
                var seasonNum = season.IndexNumber ?? 0;
                var norm = NormalizeTitle(series.Name ?? "");

                var syncOverride = GetSyncOverride(userCfg, seriesId, seasonNum);
                var isPinned = syncOverride?.PinnedMalId != null;
                var isBlocked = syncOverride?.Blocked == true;

                string? malId = null;
                string? malIdSource = "none";
                string? malTitle = null;
                string? malImageUrl = null;

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

                    // Cache
                    if (malId is null)
                    {
                        var cached = GetCachedEntry(userId, norm, seasonNum, cfg.CacheTtlDays);
                        if (cached is not null)
                        {
                            malId = cached.MalId;
                            malTitle = cached.MalTitle;
                            malImageUrl = cached.MalImageUrl;
                            malIdSource = "cache";
                        }
                    }
                }

                var rangeMap = userCfg.EpisodeRangeMappings
                    .FirstOrDefault(m => m.JellyfinSeriesId == seriesId && m.SeasonNumber == seasonNum);

                mappings.Add(new SeasonMapping
                {
                    SeasonNumber = seasonNum,
                    MalId = malId,
                    MalTitle = malTitle,
                    MalImageUrl = malImageUrl,
                    MalIdSource = malIdSource ?? "none",
                    Pinned = isPinned,
                    Blocked = isBlocked,
                    IsSpecial = seasonNum == 0,
                    EpisodeRanges = rangeMap?.Ranges
                        .Select(r => new EpisodeRangeInfo(r.Id, r.EpisodeFrom, r.EpisodeTo, r.MalId, r.MalTitle, r.MalImageUrl))
                        .ToList(),
                });
            }

            result.Add(new SeriesMapping
            {
                JellyfinSeriesId = seriesId,
                JellyfinSeriesName = series.Name ?? "",
                Seasons = mappings,
            });
        }

        return result;
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

                // Find best sequel: prefer one not yet visited
                var nextSequel = node.RelatedAnime?
                    .Where(r => r.RelationType is "sequel"
                             && !visited.Contains(r.Node.Id.ToString()))
                    .FirstOrDefault();

                if (nextSequel is null) break;

                var nextId = nextSequel.Node.Id.ToString();
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
        return entry?.MalId;
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
        string? malTitle = null, string? malImageUrl = null)
    {
        var key = $"{userScope}::{series}::{season}";
        var entry = new CacheEntry(malId, DateTime.UtcNow) { MalTitle = malTitle, MalImageUrl = malImageUrl };
        _malIdCache[key] = entry;
        _persistentCache[key] = entry;
        _ = SavePersistentCacheAsync();
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
        public string MalIdSource { get; set; } = "none";
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
