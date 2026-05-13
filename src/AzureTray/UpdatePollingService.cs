using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;

namespace AzureTray;

// Re-runs IUpdateService.CheckOnStartupAsync on a fixed cadence so a
// long-running tray process still sees new releases without a restart.
// Startup check is still triggered by App.OnStartup; this loop starts
// after the first interval has elapsed so the two don't race at launch.
// The check is a no-op once a pending version is already surfaced —
// UpdateService dedupes by version string.
internal sealed class UpdatePollingService : BackgroundService
{
    private readonly IUpdateService _updateService;
    private readonly UpdateFeedOptions _options;
    private readonly ILogger<UpdatePollingService> _logger;

    public UpdatePollingService(
        IUpdateService updateService,
        IOptions<UpdateFeedOptions> options,
        ILogger<UpdatePollingService> logger)
    {
        _updateService = updateService;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.CheckIntervalHours <= 0)
        {
            _logger.LogInformation("Update polling disabled (CheckIntervalHours <= 0).");
            return;
        }

        var interval = TimeSpan.FromHours(_options.CheckIntervalHours);
        _logger.LogInformation(
            "Update polling enabled; checking every {Interval}.",
            interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            try
            {
                await _updateService.CheckOnStartupAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // UpdateService already logs inside CheckOnStartupAsync, but
                // belt-and-braces — we never want the loop to crash on a
                // transient network error.
                _logger.LogWarning(ex, "Periodic update check failed; will retry next interval.");
            }
        }
    }
}
