using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.MalSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Channels;

namespace Jellyfin.Plugin.MalSync.Api;

/// <summary>
/// REST endpoints consumed by the plugin's config page (configPage.html).
/// All routes live under /MalSync/…
/// </summary>
[ApiController]
[Route("MalSync")]
public sealed class MalSyncController : ControllerBase
{
    private readonly MalAuthService _auth;
    private readonly MalSyncService _sync;
    private readonly JellyseerrImportService _jellyseerr;
    private readonly ILibraryManager _libraryManager;
    private readonly ITaskManager _taskManager;
    private readonly IUserManager _userManager;

    public MalSyncController(
        MalAuthService auth,
        MalSyncService sync,
        JellyseerrImportService jellyseerr,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        IUserManager userManager)
    {
        _auth = auth;
        _sync = sync;
        _jellyseerr = jellyseerr;
        _libraryManager = libraryManager;
        _taskManager = taskManager;
        _userManager = userManager;
    }

    // ── GET /MalSync/status ───────────────────────────────────────────────
    /// <summary>Returns the current token status for the calling user.</summary>
    [HttpGet("status")]
    [Authorize]
    public IActionResult GetStatus()
    {
        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);
        var hasToken = !string.IsNullOrEmpty(uc.MalAccessToken);
        return Ok(new
        {
            authenticated = hasToken,
            malUsername = uc.MalUsername,
            tokenExpires = hasToken ? uc.TokenExpiresAt.ToString("o") : null,
        });
    }

    // ── GET /MalSync/auth/start ───────────────────────────────────────────
    /// <summary>Generates a MAL authorization URL and returns it.</summary>
    [HttpGet("auth/start")]
    [Authorize]
    public IActionResult StartAuth()
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;
        if (string.IsNullOrEmpty(cfg.MalClientId))
            return BadRequest(new { error = "MAL Client-ID is not configured. Please save your Client-ID first." });

        var userId = GetUserId();
        var url = _auth.GetAuthorizationUrl(userId, cfg.MalClientId);
        return Ok(new { authUrl = url });
    }

    // ── POST /MalSync/auth/callback ───────────────────────────────────────
    /// <summary>Exchanges the authorization code from the MAL redirect URL.</summary>
    [HttpPost("auth/callback")]
    [Authorize]
    public async Task<IActionResult> AuthCallback([FromBody] AuthCallbackRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Code))
            return BadRequest(new { error = "Missing authorization code." });

        var cfg = MalSyncPlugin.Instance!.Configuration;
        var userId = GetUserId();
        var (ok, msg) = await _auth.ExchangeCodeAsync(userId, cfg.MalClientId, body.Code)
                                   .ConfigureAwait(false);

        if (!ok) return BadRequest(new { error = msg });

        // Fetch MAL username to display in the UI
        try
        {
            var token = await _auth.GetAccessTokenAsync(userId).ConfigureAwait(false);
            // username fetch could be added here if needed
        }
        catch { /* non-fatal */ }

        return Ok(new { message = msg });
    }

    // ── POST /MalSync/auth/disconnect ─────────────────────────────────────
    /// <summary>Removes the stored MAL tokens for the calling user.</summary>
    [HttpPost("auth/disconnect")]
    [Authorize]
    public IActionResult Disconnect()
    {
        var userId = GetUserId();
        var cfg = MalSyncPlugin.Instance!.Configuration;
        cfg.UserConfigs.RemoveAll(u => u.UserId == userId);
        MalSyncPlugin.Instance.SaveConfiguration();
        return Ok(new { message = "MAL account disconnected." });
    }

    // ── GET /MalSync/libraries ────────────────────────────────────────────
    /// <summary>Returns all Jellyfin library folder paths (for the anime-paths picker).</summary>
    [HttpGet("libraries")]
    [Authorize]
    public IActionResult GetLibraries()
    {
        var paths = _libraryManager.GetVirtualFolders()
            .SelectMany(f => f.Locations ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct()
            .OrderBy(p => p)
            .ToList();
        return Ok(new { paths });
    }

    // ── GET /MalSync/config ───────────────────────────────────────────────
    /// <summary>Returns the current global plugin configuration.</summary>
    [HttpGet("config")]
    [Authorize]
    public IActionResult GetConfig()
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;
        return Ok(new
        {
            malClientId = cfg.MalClientId,
            malSearchMinSimilarity = cfg.MalSearchMinSimilarity,
            malNoDowngrade = cfg.MalNoDowngrade,
            jfUpdateWatched = cfg.JfUpdateWatched,
            animePaths = cfg.AnimePaths,
            cacheTtlDays = cfg.CacheTtlDays,
            syncHour = cfg.SyncHour,
            syncMinute = cfg.SyncMinute,
            syncUseInterval = cfg.SyncUseInterval,
            syncIntervalMinutes = cfg.SyncIntervalMinutes,
            jellyseerrUrl = cfg.JellyseerrUrl,
            jellyseerrApiKey = cfg.JellyseerrApiKey,
        });
    }

    // ── POST /MalSync/config ──────────────────────────────────────────────
    /// <summary>Saves global plugin configuration (admin only).</summary>
    [HttpPost("config")]
    [Authorize]
    public IActionResult SaveConfig([FromBody] ConfigRequest body)
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;

        if (!string.IsNullOrWhiteSpace(body.MalClientId))
            cfg.MalClientId = body.MalClientId.Trim();
        if (body.MalSearchMinSimilarity.HasValue)
            cfg.MalSearchMinSimilarity = Math.Clamp(body.MalSearchMinSimilarity.Value, 0.0, 1.0);
        if (body.MalNoDowngrade.HasValue)
            cfg.MalNoDowngrade = body.MalNoDowngrade.Value;
        if (body.JfUpdateWatched.HasValue)
            cfg.JfUpdateWatched = body.JfUpdateWatched.Value;
        if (body.AnimePaths is not null)
            cfg.AnimePaths = body.AnimePaths.Trim();
        if (body.CacheTtlDays.HasValue)
            cfg.CacheTtlDays = Math.Max(1, body.CacheTtlDays.Value);
        if (body.SyncHour.HasValue)
            cfg.SyncHour = Math.Clamp(body.SyncHour.Value, 0, 23);
        if (body.SyncMinute.HasValue)
            cfg.SyncMinute = Math.Clamp(body.SyncMinute.Value, 0, 59);
        if (body.SyncUseInterval.HasValue)
            cfg.SyncUseInterval = body.SyncUseInterval.Value;
        if (body.SyncIntervalMinutes.HasValue)
            cfg.SyncIntervalMinutes = Math.Clamp(body.SyncIntervalMinutes.Value, 5, 10080);
        if (body.JellyseerrUrl is not null)
            cfg.JellyseerrUrl = body.JellyseerrUrl.Trim().TrimEnd('/');
        if (body.JellyseerrApiKey is not null)
            cfg.JellyseerrApiKey = body.JellyseerrApiKey.Trim();

        MalSyncPlugin.Instance.SaveConfiguration();

        // Apply the new schedule to the running task immediately.
        var task = _taskManager.ScheduledTasks
            .FirstOrDefault(t => t.ScheduledTask is Tasks.MalSyncTask);
        if (task is not null)
        {
            task.Triggers = cfg.SyncUseInterval
                ? [new TaskTriggerInfo
                  {
                      Type = TaskTriggerInfoType.IntervalTrigger,
                      IntervalTicks = TimeSpan.FromMinutes(cfg.SyncIntervalMinutes).Ticks,
                  }]
                : [new TaskTriggerInfo
                  {
                      Type = TaskTriggerInfoType.DailyTrigger,
                      TimeOfDayTicks = TimeSpan
                          .FromHours(cfg.SyncHour)
                          .Add(TimeSpan.FromMinutes(cfg.SyncMinute))
                          .Ticks,
                  }];
        }

        return Ok(new { message = "Configuration saved." });
    }

    // ── POST /MalSync/sync/run ────────────────────────────────────────────
    /// <summary>Triggers an immediate sync for the calling user.</summary>
    [HttpPost("sync/run")]
    [Authorize]
    public async Task<IActionResult> RunSync([FromQuery] bool dryRun = false, [FromQuery] bool debug = false)
    {
        var userId = GetUserId();
        if (!_auth.HasValidToken(userId))
            return BadRequest(new { error = "Not authenticated with MAL. Please connect your account first." });

        var log = await _sync.SyncUserAsync(userId, dryRun, debug).ConfigureAwait(false);
        return Ok(new { log });
    }

    // ── GET /MalSync/sync/stream ──────────────────────────────────────────
    /// <summary>Streams sync log lines as Server-Sent Events (text/event-stream).</summary>
    [HttpGet("sync/stream")]
    [Authorize]
    public async Task StreamSync([FromQuery] bool dryRun = false, [FromQuery] bool debug = false)
    {
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var userId = GetUserId();
        if (!_auth.HasValidToken(userId))
        {
            await Response.WriteAsync("data: [ERROR] Not authenticated with MAL.\n\n").ConfigureAwait(false);
            return;
        }

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        var syncTask = Task.Run(async () =>
        {
            try
            {
                await _sync.SyncUserAsync(
                    userId, dryRun, debug,
                    onLog: line => channel.Writer.TryWrite(line),
                    cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(HttpContext.RequestAborted).ConfigureAwait(false))
            {
                await Response.WriteAsync($"data: {line}\n\n").ConfigureAwait(false);
                await Response.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            }
            await Response.WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
            await Response.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* client disconnected */ }

        try { await syncTask.ConfigureAwait(false); } catch { /* already handled inside */ }
    }

    // ── GET /MalSync/user/config ──────────────────────────────────────────
    /// <summary>Returns per-user sync preferences for the calling user.</summary>
    [HttpGet("user/config")]
    [Authorize]
    public IActionResult GetUserConfig()
    {
        var userId = GetUserId();
        var cfg = MalSyncPlugin.Instance!.Configuration;
        var uc = _auth.GetOrCreateUserConfig(userId);
        return Ok(new
        {
            // Return per-user value if set, otherwise the global default
            noDowngrade = uc.NoDowngrade ?? cfg.MalNoDowngrade,
            jfUpdateWatched = uc.JfUpdateWatched ?? cfg.JfUpdateWatched,
            // Indicate whether the value is a personal override or the global default
            noDowngradeIsPersonal = uc.NoDowngrade.HasValue,
            jfUpdateWatchedIsPersonal = uc.JfUpdateWatched.HasValue,
            jellyseerrProfiles = uc.JellyseerrProfiles,
        });
    }

    // ── POST /MalSync/user/config ─────────────────────────────────────────
    /// <summary>Saves per-user sync preferences for the calling user.</summary>
    [HttpPost("user/config")]
    [Authorize]
    public IActionResult SaveUserConfig([FromBody] UserConfigRequest body)
    {
        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);

        if (body.NoDowngrade.HasValue)
            uc.NoDowngrade = body.NoDowngrade.Value;
        if (body.JfUpdateWatched.HasValue)
            uc.JfUpdateWatched = body.JfUpdateWatched.Value;
        if (body.JellyseerrProfiles is not null)
        {
            uc.JellyseerrProfiles = body.JellyseerrProfiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new Configuration.JellyseerrImportProfile
                {
                    Id = string.IsNullOrWhiteSpace(p.Id) ? Guid.NewGuid().ToString("N")[..8] : p.Id,
                    Name = p.Name.Trim(),
                    Statuses = p.Statuses
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Select(s => s.Trim().ToLowerInvariant())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList(),
                    RequestAllSeasons = p.RequestAllSeasons,
                })
                .ToList();
        }

        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = "Personal settings saved." });
    }

    // ── POST /MalSync/import/run ──────────────────────────────────────────
    /// <summary>Triggers the MAL→Jellyseerr import for the calling user.</summary>
    [HttpPost("import/run")]
    [Authorize]
    public async Task<IActionResult> RunImport([FromQuery] bool dryRun = false)
    {
        var userId = GetUserId();
        if (!_auth.HasValidToken(userId))
            return BadRequest(new { error = "Not authenticated with MAL. Please connect your account first." });

        var log = await _jellyseerr.RunImportAsync(userId, dryRun).ConfigureAwait(false);
        return Ok(new { log });
    }

    // ── GET /MalSync/import/stream ────────────────────────────────────────
    /// <summary>Streams MAL→Jellyseerr import log lines as Server-Sent Events.</summary>
    [HttpGet("import/stream")]
    [Authorize]
    public async Task StreamImport([FromQuery] bool dryRun = false)
    {
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var userId = GetUserId();
        if (!_auth.HasValidToken(userId))
        {
            await Response.WriteAsync("data: [ERROR] Not authenticated with MAL.\n\n").ConfigureAwait(false);
            return;
        }

        var channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        var importTask = Task.Run(async () =>
        {
            try
            {
                await _jellyseerr.RunImportAsync(
                    userId, dryRun,
                    onLog: line => channel.Writer.TryWrite(line),
                    cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        try
        {
            await foreach (var line in channel.Reader.ReadAllAsync(HttpContext.RequestAborted).ConfigureAwait(false))
            {
                await Response.WriteAsync($"data: {line}\n\n").ConfigureAwait(false);
                await Response.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            }
            await Response.WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
            await Response.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* client disconnected */ }

        try { await importTask.ConfigureAwait(false); } catch { /* already handled inside */ }
    }

    // ── GET /MalSync/is-admin ─────────────────────────────────────────────
    /// <summary>Returns whether the calling user is a Jellyfin administrator.</summary>
    [HttpGet("is-admin")]
    [Authorize]
    public IActionResult GetIsAdmin()
    {
        var userId = GetUserId();
        if (string.IsNullOrEmpty(userId)) return Ok(new { isAdmin = false });
        var user = _userManager.GetUserById(Guid.Parse(userId));
        return Ok(new { isAdmin = user?.HasPermission(PermissionKind.IsAdministrator) ?? false });
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private string GetUserId()
    {
        // Jellyfin injects the authenticated user-id as a claim
        var claim = User.FindFirst("Jellyfin-UserId")
                 ?? User.FindFirst("sub")
                 ?? User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        return claim?.Value ?? string.Empty;
    }

    // ── Request DTOs ──────────────────────────────────────────────────────

    public sealed class AuthCallbackRequest
    {
        public string Code { get; set; } = string.Empty;
    }

    public sealed class ConfigRequest
    {
        public string? MalClientId { get; set; }
        public double? MalSearchMinSimilarity { get; set; }
        public bool? MalNoDowngrade { get; set; }
        public bool? JfUpdateWatched { get; set; }
        public string? AnimePaths { get; set; }
        public int? CacheTtlDays { get; set; }
        public int? SyncHour { get; set; }
        public int? SyncMinute { get; set; }
        public bool? SyncUseInterval { get; set; }
        public int? SyncIntervalMinutes { get; set; }
        public string? JellyseerrUrl { get; set; }
        public string? JellyseerrApiKey { get; set; }
    }

    public sealed class UserConfigRequest
    {
        public bool? NoDowngrade { get; set; }
        public bool? JfUpdateWatched { get; set; }
        public List<Configuration.JellyseerrImportProfile>? JellyseerrProfiles { get; set; }
    }
}
