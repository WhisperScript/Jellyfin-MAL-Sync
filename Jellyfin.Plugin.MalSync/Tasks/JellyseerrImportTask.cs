using Jellyfin.Plugin.MalSync.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MalSync.Tasks;

/// <summary>
/// Manually-triggered task that reads the authenticated user's MAL list and
/// submits Jellyseerr requests for seasons whose MAL status matches the
/// calling user's configured import profiles.
/// </summary>
public sealed class JellyseerrImportTask : IScheduledTask
{
    private readonly JellyseerrImportService _importService;
    private readonly MalAuthService _authService;
    private readonly ILogger<JellyseerrImportTask> _logger;

    public JellyseerrImportTask(
        JellyseerrImportService importService,
        MalAuthService authService,
        ILogger<JellyseerrImportTask> logger)
    {
        _importService = importService;
        _authService = authService;
        _logger = logger;
    }

    public string Name => "Import MAL list to Jellyseerr";
    public string Key => "MalJellyseerrImport";
    public string Description => "Runs MAL→Jellyseerr import for every authenticated user who configured personal import profiles. Requests are created as the matching Jellyfin/Jellyseerr user.";
    public string Category => "MAL Sync";

    /// <summary>Default trigger: every 12 hours.</summary>
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(12).Ticks,
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var cfg = MalSyncPlugin.Instance!.Configuration;
        var users = cfg.UserConfigs
            .Where(u => u.JellyseerrProfiles.Count > 0)
            .Select(u => u.UserId)
            .ToList();

        if (users.Count == 0)
        {
            _logger.LogInformation("MAL→Jellyseerr import: no users with personal import profiles configured, nothing to do.");
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
                _logger.LogWarning("MAL→Jellyseerr import: skipping user {UserId} – token invalid / refresh failed.", userId);
                continue;
            }

            var log = await _importService.RunImportAsync(userId, dryRun: false, cancellationToken: cancellationToken)
                                          .ConfigureAwait(false);
            foreach (var line in log)
                _logger.LogInformation("{Line}", line);

            done++;
            progress.Report(100.0 * done / users.Count);
        }

        progress.Report(100);
    }
}
