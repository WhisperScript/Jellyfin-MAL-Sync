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

    public string[] GetAnimePaths() =>
        AnimePaths.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>Per-Jellyfin-user MAL token set.</summary>
public class UserMalConfig
{
    /// <summary>Jellyfin user ID (GUID string).</summary>
    public string UserId { get; set; } = string.Empty;
    public string MalAccessToken { get; set; } = string.Empty;
    public string MalRefreshToken { get; set; } = string.Empty;
    public DateTime TokenExpiresAt { get; set; } = DateTime.MinValue;
    /// <summary>MAL username shown in the UI after successful auth.</summary>
    public string MalUsername { get; set; } = string.Empty;
}
