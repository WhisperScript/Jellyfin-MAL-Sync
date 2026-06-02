using System.Text.Json.Serialization;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MalSync.Configuration;

/// <summary>Global plugin configuration + per-user MAL token store.</summary>
public class PluginConfiguration : BasePluginConfiguration
{
    // ── MAL App credentials ────────────────────────────────────────────────
    /// <summary>MAL API client-id registered at myanimelist.net.</summary>
    public string MalClientId { get; set; } = string.Empty;

    // ── Per-user token storage ─────────────────────────────────────────────
    public List<UserMalConfig> UserConfigs { get; set; } = new();

    // ── Sync behaviour ─────────────────────────────────────────────────────
    /// <summary>Minimum title similarity (0.0–1.0) to accept a MAL search hit.</summary>
    public double MalSearchMinSimilarity { get; set; } = 0.60;

    /// <summary>Never write a lower ep-count or worse status to MAL than already there.</summary>
    public bool MalNoDowngrade { get; set; } = true;

    /// <summary>Also mark episodes played in Jellyfin when already watched on MAL.</summary>
    public bool JfUpdateWatched { get; set; } = false;

    /// <summary>Comma-separated library paths treated as anime.</summary>
    public string AnimePaths { get; set; } = string.Empty;

    /// <summary>Days before a cached MAL-ID is re-resolved.</summary>
    public int CacheTtlDays { get; set; } = 30;

    /// <summary>Hour of day (0–23, server local time) for the daily automatic sync.</summary>
    public int SyncHour { get; set; } = 3;

    /// <summary>Minute (0–59) for the daily automatic sync.</summary>
    public int SyncMinute { get; set; } = 0;

    /// <summary>When true, use an interval trigger instead of a fixed daily time.</summary>
    public bool SyncUseInterval { get; set; } = false;

    /// <summary>Repeat interval in minutes (used when SyncUseInterval is true).</summary>
    public int SyncIntervalMinutes { get; set; } = 60;

    // ── Jellyseerr / MAL Import settings ──────────────────────────────────
    /// <summary>Base URL of the Jellyseerr instance (e.g. http://jellyseerr:5055).</summary>
    public string JellyseerrUrl { get; set; } = string.Empty;

    /// <summary>Jellyseerr API key (Settings → General → API Key).</summary>
    public string JellyseerrApiKey { get; set; } = string.Empty;

    public string[] GetAnimePaths() =>
        AnimePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>One import profile: maps MAL statuses to a season-request strategy in Jellyseerr.</summary>
public class JellyseerrImportProfile
{
    /// <summary>Stable identifier (short random hex string).</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Default";
    /// <summary>MAL status values that activate this profile (e.g. "plan_to_watch", "watching").</summary>
    [JsonPropertyName("statuses")]
    public List<string> Statuses { get; set; } = new();
    /// <summary>
    /// When true, request ALL seasons of the series in Jellyseerr.
    /// When false (default), request only the season that corresponds to this MAL entry.
    /// </summary>
    [JsonPropertyName("requestAllSeasons")]
    public bool RequestAllSeasons { get; set; } = false;
}

/// <summary>Per-Jellyfin-user MAL token set and personal sync preferences.</summary>
public class UserMalConfig
{
    /// <summary>Jellyfin user ID (GUID string).</summary>
    public string UserId { get; set; } = string.Empty;
    public string MalAccessToken { get; set; } = string.Empty;
    public string MalRefreshToken { get; set; } = string.Empty;
    public DateTime TokenExpiresAt { get; set; } = DateTime.MinValue;
    /// <summary>MAL username shown in the UI after successful auth.</summary>
    public string MalUsername { get; set; } = string.Empty;

    // ── Per-user sync preferences (null = fall back to global setting) ─────
    /// <summary>Never downgrade MAL progress. null = use global default.</summary>
    public bool? NoDowngrade { get; set; } = null;
    /// <summary>Mark Jellyfin episodes from MAL list. null = use global default.</summary>
    public bool? JfUpdateWatched { get; set; } = null;

    /// <summary>
    /// Per-user Jellyseerr import profiles.
    /// Each profile maps MAL statuses to a season request strategy for this user.
    /// </summary>
    public List<JellyseerrImportProfile> JellyseerrProfiles { get; set; } = new();

    /// <summary>
    /// Per-series sync overrides: pin a specific MAL ID or block sync entirely.
    /// Keyed by Jellyfin series ID + season number.
    /// </summary>
    public List<SeriesOverride> SeriesOverrides { get; set; } = new();

    /// <summary>MAL IDs to skip during Jellyseerr import.</summary>
    public List<MalImportBlock> ImportBlocks { get; set; } = new();

    /// <summary>
    /// Episode-range-to-MAL mappings for absolute-numbered seasons
    /// (one Jellyfin season = multiple MAL entries).
    /// </summary>
    public List<EpisodeRangeMapping> EpisodeRangeMappings { get; set; } = new();

    /// <summary>Pending range-staleness notices generated by the sync task.</summary>
    public List<StaleRangeNotice> StaleRangeNotices { get; set; } = new();

    /// <summary>Discord or generic webhook URL for push notifications (optional).</summary>
    public string? WebhookUrl { get; set; }

    /// <summary>Send webhook when episode ranges are likely outdated.</summary>
    public bool WebhookOnStaleRanges   { get; set; } = true;
    /// <summary>Send webhook when MAL API errors occur during sync.</summary>
    public bool WebhookOnSyncErrors    { get; set; } = false;
    /// <summary>Send webhook with a summary of updated MAL entries after each sync.</summary>
    public bool WebhookOnSyncSummary   { get; set; } = false;
    /// <summary>Send webhook when Jellyseerr request errors occur during import.</summary>
    public bool WebhookOnImportErrors  { get; set; } = false;
    /// <summary>Send webhook with a summary of requested entries after each import.</summary>
    public bool WebhookOnImportSummary { get; set; } = false;
}

/// <summary>
/// Override the automatic MAL ID resolution for a specific Jellyfin series/season.
/// Used to fix wrong matches or block series from being synced.
/// </summary>
public class SeriesOverride
{
    /// <summary>Jellyfin series ID (GUID string).</summary>
    [JsonPropertyName("jellyfinSeriesId")]
    public string JellyfinSeriesId { get; set; } = string.Empty;

    /// <summary>Display name of the series (informational only).</summary>
    [JsonPropertyName("jellyfinSeriesName")]
    public string JellyfinSeriesName { get; set; } = string.Empty;

    /// <summary>Season number. 0 means all seasons of this series.</summary>
    [JsonPropertyName("seasonNumber")]
    public int SeasonNumber { get; set; } = 0;

    /// <summary>Manually assigned MAL ID. null = no pin (automatic resolution).</summary>
    [JsonPropertyName("pinnedMalId")]
    public string? PinnedMalId { get; set; }

    /// <summary>MAL title of the pinned entry (for display only).</summary>
    [JsonPropertyName("pinnedMalTitle")]
    public string? PinnedMalTitle { get; set; }

    /// <summary>MAL cover image URL of the pinned entry (for display only).</summary>
    [JsonPropertyName("pinnedMalImageUrl")]
    public string? PinnedMalImageUrl { get; set; }

    /// <summary>When true, skip this series/season entirely during sync.</summary>
    [JsonPropertyName("blocked")]
    public bool Blocked { get; set; } = false;
}

/// <summary>
/// Maps episode ranges within one Jellyfin season to separate MAL entries.
/// Used for absolute-numbered shows (e.g. Sonarr TVDB structure) where one Jellyfin
/// season contains multiple MAL cours/parts.
/// </summary>
public class EpisodeRangeMapping
{
    [JsonPropertyName("jellyfinSeriesId")]   public string JellyfinSeriesId   { get; set; } = string.Empty;
    [JsonPropertyName("jellyfinSeriesName")] public string JellyfinSeriesName { get; set; } = string.Empty;
    [JsonPropertyName("seasonNumber")]       public int    SeasonNumber        { get; set; }
    [JsonPropertyName("ranges")]             public List<EpisodeRange> Ranges  { get; set; } = new();
}

/// <summary>One episode range → one MAL entry within a season.</summary>
public class EpisodeRange
{
    [JsonPropertyName("id")]          public string  Id          { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>First episode number (1-based, inclusive).</summary>
    [JsonPropertyName("episodeFrom")] public int     EpisodeFrom { get; set; } = 1;
    /// <summary>Last episode number (inclusive). 0 = no upper limit.</summary>
    [JsonPropertyName("episodeTo")]   public int     EpisodeTo   { get; set; } = 0;
    [JsonPropertyName("malId")]       public string  MalId       { get; set; } = string.Empty;
    [JsonPropertyName("malTitle")]    public string? MalTitle    { get; set; }
    [JsonPropertyName("malImageUrl")] public string? MalImageUrl { get; set; }
}

/// <summary>Notification shown on the plugin page when episode ranges are likely outdated.</summary>
public class StaleRangeNotice
{
    [JsonPropertyName("jellyfinSeriesId")]   public string   JellyfinSeriesId   { get; set; } = string.Empty;
    [JsonPropertyName("jellyfinSeriesName")] public string   JellyfinSeriesName { get; set; } = string.Empty;
    [JsonPropertyName("seasonNumber")]       public int      SeasonNumber       { get; set; }
    [JsonPropertyName("malTitle")]           public string   MalTitle           { get; set; } = string.Empty;
    [JsonPropertyName("detectedAt")]         public DateTime DetectedAt         { get; set; } = DateTime.UtcNow;
}

/// <summary>Block a specific MAL anime from being requested during Jellyseerr import.</summary>
public class MalImportBlock
{
    /// <summary>MAL anime ID.</summary>
    [JsonPropertyName("malId")]
    public string MalId { get; set; } = string.Empty;

    /// <summary>MAL title (for display only).</summary>
    [JsonPropertyName("malTitle")]
    public string? MalTitle { get; set; }
}
