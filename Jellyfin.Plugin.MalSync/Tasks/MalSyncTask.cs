using Jellyfin.Plugin.MalSync.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MalSync.Tasks;

/// <summary>
/// Scheduled task that runs the MAL sync for all authenticated users.
/// Visible in Jellyfin → Dashboard → Scheduled Tasks.
/// </summary>
public sealed class MalSyncTask : IScheduledTask
{
    private readonly MalSyncService _syncService;
    private readonly MalAuthService _authService;
    private readonly IUserManager _userManager;
    private readonly ILogger<MalSyncTask> _logger;

    public MalSyncTask(
        MalSyncService syncService,
        MalAuthService authService,
        IUserManager userManager,
        ILogger<MalSyncTask> logger)
    {
        _syncService = syncService;
        _authService = authService;
        _userManager = userManager;
        _logger = logger;
    }

    public string Name => "Sync watch progress to MyAnimeList";
    public string Key => "MalSync";
    public string Description => "Pushes Jellyfin watch counts and statuses to MyAnimeList for all connected users.";
    public string Category => "MAL Sync";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks,
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;
        var users = cfg.UserConfigs.Select(u => u.UserId).ToList();

        if (users.Count == 0)
        {
            _logger.LogInformation("MAL Sync: no users configured, nothing to do.");
            progress.Report(100);
            return;
        }

        var done = 0;
        foreach (var userId in users)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_authService.HasValidToken(userId)
                && !await _authService.RefreshTokenAsync(userId).ConfigureAwait(false))
            {
                _logger.LogWarning("MAL Sync: skipping user {UserId} – token invalid / refresh failed.", userId);
                continue;
            }

            var log = await _syncService.SyncUserAsync(userId, dryRun: false, cancellationToken: cancellationToken)
                                        .ConfigureAwait(false);
            foreach (var line in log)
                _logger.LogInformation("{Line}", line);

            done++;
            progress.Report(100.0 * done / users.Count);
        }

        progress.Report(100);
    }
}
