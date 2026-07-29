using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;
using AzureTray.Notifications;

namespace AzureTray.Extensions;

// Periodically asks whether any installed plugin has a newer version on
// nuget.org, mirroring UpdatePollingService's shape for the host's own
// releases: interval from config, 0 disables, one full interval of sleep
// before the first tick (so it never races the Settings window's own check at
// launch), and any error swallowed so the loop survives a flaky network.
//
// Dedupe: pluginId → last version we notified about, so finding the same
// update every tick raises one toast, not one per hour. Same idea as
// UpdateService comparing PendingUpdateVersion against the new version.
internal sealed class PluginUpdatePollingService : BackgroundService
{
    private readonly IPluginUpdateChecker _checker;
    private readonly PluginUpdateState _state;
    private readonly PluginUpdateNotifier _notifier;
    private readonly PluginAutoUpdater _autoUpdater;
    private readonly PluginUpdatePreferenceStore _preferences;
    private readonly PluginOptions _options;
    private readonly ILogger<PluginUpdatePollingService> _logger;

    private readonly Dictionary<string, string> _lastNotifiedVersions =
        new(StringComparer.OrdinalIgnoreCase);

    public PluginUpdatePollingService(
        IPluginUpdateChecker checker,
        PluginUpdateState state,
        PluginUpdateNotifier notifier,
        PluginAutoUpdater autoUpdater,
        PluginUpdatePreferenceStore preferences,
        IOptions<PluginOptions> options,
        ILogger<PluginUpdatePollingService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _checker = checker;
        _state = state;
        _notifier = notifier;
        _autoUpdater = autoUpdater;
        _preferences = preferences;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.UpdateCheckIntervalHours <= 0)
        {
            _logger.LogInformation("Plugin update polling disabled (App:Plugins:UpdateCheckIntervalHours <= 0).");
            return;
        }

        var interval = TimeSpan.FromHours(_options.UpdateCheckIntervalHours);
        _logger.LogInformation("Plugin update polling enabled; checking every {Interval}.", interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Periodic plugin update check failed; will retry next interval.");
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var updates = await _checker.CheckAsync(cancellationToken).ConfigureAwait(false);

        // Publish the full set so the Settings banner reflects reality even
        // for updates we've already toasted about.
        _state.Publish(updates);

        var fresh = updates
            .Where(u => !_lastNotifiedVersions.TryGetValue(u.PackageId, out var notified)
                        || !string.Equals(notified, u.LatestVersion, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (fresh.Length == 0) return;

        foreach (var update in fresh)
        {
            _lastNotifiedVersions[update.PackageId] = update.LatestVersion;
        }

        if (!_preferences.AutoUpdateEnabled)
        {
            _notifier.ShowUpdatesAvailable(fresh);
            return;
        }

        // Auto-update owns its own reporting: one toast for what it applied,
        // one for what it refused to touch unattended.
        var applied = await _autoUpdater.ApplyAsync(fresh, cancellationToken).ConfigureAwait(false);
        foreach (var update in applied)
        {
            _state.Remove(update.PackageId);
        }
    }
}
