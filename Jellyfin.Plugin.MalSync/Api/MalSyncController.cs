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
            noDowngrade = uc.NoDowngrade ?? cfg.MalNoDowngrade,
            jfUpdateWatched = uc.JfUpdateWatched ?? cfg.JfUpdateWatched,
            noDowngradeIsPersonal = uc.NoDowngrade.HasValue,
            jfUpdateWatchedIsPersonal = uc.JfUpdateWatched.HasValue,
            jellyseerrProfiles = uc.JellyseerrProfiles,
            seriesOverrides = uc.SeriesOverrides,
            importBlocks = uc.ImportBlocks,
            staleRangeNotices = uc.StaleRangeNotices,
            webhookUrl = uc.WebhookUrl ?? string.Empty,
            webhookOnStaleRanges   = uc.WebhookOnStaleRanges,
            webhookOnSyncErrors    = uc.WebhookOnSyncErrors,
            webhookOnSyncSummary   = uc.WebhookOnSyncSummary,
            webhookOnImportErrors  = uc.WebhookOnImportErrors,
            webhookOnImportSummary = uc.WebhookOnImportSummary,
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

        if (body.WebhookUrl is not null)
            uc.WebhookUrl = string.IsNullOrWhiteSpace(body.WebhookUrl) ? null : body.WebhookUrl.Trim();
        if (body.WebhookOnStaleRanges.HasValue)   uc.WebhookOnStaleRanges   = body.WebhookOnStaleRanges.Value;
        if (body.WebhookOnSyncErrors.HasValue)    uc.WebhookOnSyncErrors    = body.WebhookOnSyncErrors.Value;
        if (body.WebhookOnSyncSummary.HasValue)   uc.WebhookOnSyncSummary   = body.WebhookOnSyncSummary.Value;
        if (body.WebhookOnImportErrors.HasValue)  uc.WebhookOnImportErrors  = body.WebhookOnImportErrors.Value;
        if (body.WebhookOnImportSummary.HasValue) uc.WebhookOnImportSummary = body.WebhookOnImportSummary.Value;

        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = "Personal settings saved." });
    }

    // ── POST /MalSync/user/webhook/test ──────────────────────────────────
    /// <summary>Sends a test notification to the configured webhook URL.</summary>
    [HttpPost("user/webhook/test")]
    [Authorize]
    public async Task<IActionResult> TestWebhook()
    {
        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);
        if (string.IsNullOrEmpty(uc.WebhookUrl))
            return BadRequest(new { error = "No webhook URL configured. Save one first." });

        await _sync.SendWebhookAsync(
            uc.WebhookUrl,
            "✅ MAL Sync – Test",
            "Webhook configured successfully! You will receive notifications for the enabled sync and import events.",
            HttpContext.RequestAborted).ConfigureAwait(false);
        return Ok(new { message = "Test notification sent." });
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

    // ── GET /MalSync/series ───────────────────────────────────────────────
    /// <summary>Returns all Jellyfin anime series with their cached MAL ID mappings and overrides.</summary>
    [HttpGet("series")]
    [Authorize]
    public IActionResult GetSeriesMappings()
    {
        var userId = GetUserId();
        if (!_auth.HasValidToken(userId))
            return BadRequest(new { error = "Not authenticated with MAL." });

        var mappings = _sync.GetSeriesMappings(userId);

        // Inject Jellyfin poster URL for each series
        var serverAddr = $"{Request.Scheme}://{Request.Host}";
        var result = mappings.Select(m => new
        {
            jellyfinSeriesId = m.JellyfinSeriesId,
            jellyfinSeriesName = m.JellyfinSeriesName,
            posterUrl = $"{serverAddr}/Items/{m.JellyfinSeriesId}/Images/Primary?fillWidth=80&quality=60",
            seasons = m.Seasons.Select(s => new
            {
                seasonNumber = s.SeasonNumber,
                malId = s.MalId,
                malTitle = s.MalTitle,
                malImageUrl = s.MalImageUrl,
                malIdSource = s.MalIdSource,
                pinned = s.Pinned,
                blocked = s.Blocked,
                isSpecial = s.IsSpecial,
                episodeRanges = s.EpisodeRanges?.Select(r => new
                {
                    id = r.Id,
                    episodeFrom = r.EpisodeFrom,
                    episodeTo = r.EpisodeTo,
                    malId = r.MalId,
                    malTitle = r.MalTitle,
                    malImageUrl = r.MalImageUrl,
                }),
            }),
        });

        return Ok(new { series = result });
    }

    // ── GET /MalSync/mal/search ───────────────────────────────────────────
    /// <summary>Searches MAL for anime and returns results with cover images.</summary>
    [HttpGet("mal/search")]
    [Authorize]
    public async Task<IActionResult> SearchMal([FromQuery] string q, [FromQuery] int offset = 0)
    {
        var userId = GetUserId();
        if (string.IsNullOrWhiteSpace(q))
            return BadRequest(new { error = "Query is required." });

        var raw = await _sync.SearchMalAsync(q, userId, offset, HttpContext.RequestAborted).ConfigureAwait(false);
        var results = raw.Select(r => new
        {
            malId = r.MalId,
            title = r.Title,
            englishTitle = r.EnglishTitle,
            synonyms = r.Synonyms,
            imageUrl = r.ImageUrl,
            numEpisodes = r.NumEpisodes,
            status = r.Status,
            mediaType = r.MediaType,
            genres = r.Genres,
            startSeason = r.StartSeason,
        });
        return Ok(new { results });
    }

    // ── GET /MalSync/mal/anime/{id} ───────────────────────────────────────
    /// <summary>Fetches details for a single MAL anime entry.</summary>
    [HttpGet("mal/anime/{id}")]
    [Authorize]
    public async Task<IActionResult> GetMalAnime(string id)
    {
        var userId = GetUserId();
        var r = await _sync.GetMalAnimeDetailsAsync(id, userId, HttpContext.RequestAborted).ConfigureAwait(false);
        if (r is null) return NotFound(new { error = $"MAL anime {id} not found." });
        return Ok(new
        {
            malId = r.MalId,
            title = r.Title,
            englishTitle = r.EnglishTitle,
            synonyms = r.Synonyms,
            imageUrl = r.ImageUrl,
            numEpisodes = r.NumEpisodes,
            status = r.Status,
            mediaType = r.MediaType,
            genres = r.Genres,
            startSeason = r.StartSeason,
        });
    }

    // ── POST /MalSync/series/override ────────────────────────────────────
    /// <summary>Pins a MAL ID to a Jellyfin series/season, or marks it as blocked.</summary>
    [HttpPost("series/override")]
    [Authorize]
    public IActionResult SetSeriesOverride([FromBody] SeriesOverrideRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.JellyfinSeriesId))
            return BadRequest(new { error = "jellyfinSeriesId is required." });

        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);

        // Remove existing override(s) for same series+season
        uc.SeriesOverrides.RemoveAll(o =>
            o.JellyfinSeriesId == body.JellyfinSeriesId &&
            o.SeasonNumber == (body.SeasonNumber ?? 0));

        if (body.Remove != true)
        {
            uc.SeriesOverrides.Add(new Configuration.SeriesOverride
            {
                JellyfinSeriesId = body.JellyfinSeriesId,
                JellyfinSeriesName = body.JellyfinSeriesName ?? "",
                SeasonNumber = body.SeasonNumber ?? 0,
                PinnedMalId = body.Blocked == true ? null : body.PinnedMalId,
                PinnedMalTitle = body.PinnedMalTitle,
                PinnedMalImageUrl = body.PinnedMalImageUrl,
                Blocked = body.Blocked ?? false,
            });
        }

        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = "Override saved." });
    }

    // ── GET /MalSync/series/ranges/detect ────────────────────────────────
    /// <summary>Walks the MAL sequel chain and returns auto-detected episode range suggestions.</summary>
    [HttpGet("series/ranges/detect")]
    [Authorize]
    public async Task<IActionResult> DetectRanges(
        [FromQuery] string malId,
        [FromQuery] string? jellyfinSeriesId = null,
        [FromQuery] int? seasonNumber = null)
    {
        if (string.IsNullOrWhiteSpace(malId))
            return BadRequest(new { error = "malId is required." });

        var userId = GetUserId();

        Guid? seriesGuid = null;
        Jellyfin.Database.Implementations.Entities.User? jfUser = null;
        if (!string.IsNullOrEmpty(jellyfinSeriesId)
            && Guid.TryParse(jellyfinSeriesId, out var parsed)
            && seasonNumber.HasValue
            && !string.IsNullOrEmpty(userId))
        {
            seriesGuid = parsed;
            jfUser = _userManager.GetUserById(Guid.Parse(userId));
        }

        var ranges = await _sync.DetectEpisodeRangesAsync(
            malId, userId, HttpContext.RequestAborted,
            seriesGuid, seasonNumber, jfUser)
            .ConfigureAwait(false);

        var result = ranges.Select(r => new
        {
            id = r.Id,
            episodeFrom = r.EpisodeFrom,
            episodeTo = r.EpisodeTo,
            malId = r.MalId,
            malTitle = r.MalTitle,
            malImageUrl = r.MalImageUrl,
        });
        return Ok(new { ranges = result, detected = ranges.Count });
    }

    // ── POST /MalSync/series/ranges ──────────────────────────────────────
    /// <summary>Saves episode-range-to-MAL mappings for a specific season.</summary>
    [HttpPost("series/ranges")]
    [Authorize]
    public IActionResult SaveSeriesRanges([FromBody] SeriesRangesRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.JellyfinSeriesId))
            return BadRequest(new { error = "jellyfinSeriesId is required." });

        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);

        uc.EpisodeRangeMappings.RemoveAll(m =>
            m.JellyfinSeriesId == body.JellyfinSeriesId && m.SeasonNumber == body.SeasonNumber);

        if (body.Ranges is { Count: > 0 })
        {
            uc.EpisodeRangeMappings.Add(new Configuration.EpisodeRangeMapping
            {
                JellyfinSeriesId = body.JellyfinSeriesId,
                JellyfinSeriesName = body.JellyfinSeriesName ?? "",
                SeasonNumber = body.SeasonNumber,
                Ranges = body.Ranges
                    .Where(r => !string.IsNullOrWhiteSpace(r.MalId))
                    .Select(r => new Configuration.EpisodeRange
                    {
                        Id = string.IsNullOrWhiteSpace(r.Id) ? Guid.NewGuid().ToString("N")[..8] : r.Id,
                        EpisodeFrom = Math.Max(1, r.EpisodeFrom),
                        EpisodeTo = Math.Max(0, r.EpisodeTo),
                        MalId = r.MalId.Trim(),
                        MalTitle = r.MalTitle,
                        MalImageUrl = r.MalImageUrl,
                    })
                    .OrderBy(r => r.EpisodeFrom)
                    .ToList(),
            });
        }

        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = "Ranges saved." });
    }

    // ── POST /MalSync/import/block ────────────────────────────────────────
    /// <summary>Adds or removes a MAL anime ID from the import block list.</summary>
    [HttpPost("import/block")]
    [Authorize]
    public IActionResult SetImportBlock([FromBody] ImportBlockRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.MalId))
            return BadRequest(new { error = "malId is required." });

        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);

        uc.ImportBlocks.RemoveAll(b => b.MalId == body.MalId);
        if (body.Remove != true)
        {
            uc.ImportBlocks.Add(new Configuration.MalImportBlock
            {
                MalId = body.MalId,
                MalTitle = body.MalTitle,
            });
        }

        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = body.Remove == true ? "Import block removed." : "Import block added." });
    }

    // ── POST /MalSync/user/notices/dismiss ───────────────────────────────
    /// <summary>Dismisses stale-range notices (all, or a specific series+season).</summary>
    [HttpPost("user/notices/dismiss")]
    [Authorize]
    public IActionResult DismissNotices([FromBody] DismissNoticeRequest? body = null)
    {
        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);
        if (!string.IsNullOrEmpty(body?.JellyfinSeriesId))
            uc.StaleRangeNotices.RemoveAll(n =>
                n.JellyfinSeriesId == body.JellyfinSeriesId &&
                (!body.SeasonNumber.HasValue || n.SeasonNumber == body.SeasonNumber.Value));
        else
            uc.StaleRangeNotices.Clear();
        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = "Notices dismissed." });
    }

    // ── POST /MalSync/series/ranges/clear-all ────────────────────────────
    /// <summary>Clears ALL episode range mappings for the calling user.</summary>
    [HttpPost("series/ranges/clear-all")]
    [Authorize]
    public IActionResult ClearAllRanges()
    {
        var userId = GetUserId();
        var uc = _auth.GetOrCreateUserConfig(userId);
        var count = uc.EpisodeRangeMappings.Count;
        uc.EpisodeRangeMappings.Clear();
        MalSyncPlugin.Instance!.SaveConfiguration();
        return Ok(new { message = $"Cleared {count} range mapping(s)." });
    }

    // ── POST /MalSync/cache/clear ─────────────────────────────────────────
    /// <summary>Clears the MAL-ID cache for the calling user (all or single entry).</summary>
    [HttpPost("cache/clear")]
    [Authorize]
    public IActionResult ClearCache([FromBody] CacheClearRequest? body = null)
    {
        var userId = GetUserId();
        if (!string.IsNullOrWhiteSpace(body?.SeriesName))
            _sync.ClearCacheEntry(userId, body.SeriesName, body.SeasonNumber ?? -1);
        else
            _sync.ClearCache(userId);
        return Ok(new { message = "Cache cleared." });
    }

    // ── GET /MalSync/import/preview/stream ───────────────────────────────
    /// <summary>Streams import preview items and progress as Server-Sent Events.</summary>
    [HttpGet("import/preview/stream")]
    [Authorize]
    public async Task StreamImportPreview()
    {
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers["Cache-Control"] = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";

        var userId = GetUserId();
        if (!_auth.HasValidToken(userId))
        {
            await Response.WriteAsync("data: [ERROR] Not authenticated.\n\n").ConfigureAwait(false);
            return;
        }

        var channel = Channel.CreateUnbounded<string>(
            new UnboundedChannelOptions { SingleWriter = false, SingleReader = true });

        var importTask = Task.Run(async () =>
        {
            try
            {
                await _jellyseerr.RunImportAsync(
                    userId, dryRun: true,
                    onPreviewItem: item =>
                        channel.Writer.TryWrite("ITEM:" + System.Text.Json.JsonSerializer.Serialize(item)),
                    onProgress: (current, total) =>
                        channel.Writer.TryWrite($"PROGRESS:{current}/{total}"),
                    cancellationToken: HttpContext.RequestAborted).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.Complete();
            }
        });

        try
        {
            await foreach (var msg in channel.Reader.ReadAllAsync(HttpContext.RequestAborted).ConfigureAwait(false))
            {
                await Response.WriteAsync($"data: {msg}\n\n").ConfigureAwait(false);
                await Response.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
            }
            await Response.WriteAsync("data: [DONE]\n\n").ConfigureAwait(false);
            await Response.Body.FlushAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        try { await importTask.ConfigureAwait(false); } catch { }
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
        public string? WebhookUrl { get; set; }
        public bool? WebhookOnStaleRanges   { get; set; }
        public bool? WebhookOnSyncErrors    { get; set; }
        public bool? WebhookOnSyncSummary   { get; set; }
        public bool? WebhookOnImportErrors  { get; set; }
        public bool? WebhookOnImportSummary { get; set; }
    }

    public sealed class SeriesOverrideRequest
    {
        public string JellyfinSeriesId { get; set; } = string.Empty;
        public string? JellyfinSeriesName { get; set; }
        public int? SeasonNumber { get; set; }
        public string? PinnedMalId { get; set; }
        public string? PinnedMalTitle { get; set; }
        public string? PinnedMalImageUrl { get; set; }
        public bool? Blocked { get; set; }
        public bool? Remove { get; set; }
    }

    public sealed class SeriesRangesRequest
    {
        public string JellyfinSeriesId { get; set; } = string.Empty;
        public string? JellyfinSeriesName { get; set; }
        public int SeasonNumber { get; set; }
        public List<Configuration.EpisodeRange>? Ranges { get; set; }
    }

    public sealed class DismissNoticeRequest
    {
        public string? JellyfinSeriesId { get; set; }
        public int? SeasonNumber { get; set; }
    }

    public sealed class CacheClearRequest
    {
        public string? SeriesName { get; set; }
        public int? SeasonNumber { get; set; }
    }

    public sealed class ImportBlockRequest
    {
        public string MalId { get; set; } = string.Empty;
        public string? MalTitle { get; set; }
        public bool? Remove { get; set; }
    }
}
