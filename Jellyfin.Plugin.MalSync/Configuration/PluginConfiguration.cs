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

}
