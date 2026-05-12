using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Notifications;

// Surfaces an actionable notification when the update service detects a
// new release. Subscribes to IUpdateService.UpdateAvailable at host start;
// unsubscribes on stop. The notification:
//   * Has Update severity → blue accent stripe + blue "Update now" button.
//   * Is an ActionRequest, so it never auto-dismisses (the InformationRequest
//     timer in NotificationService doesn't fire for this type).
//   * Closing it (X) leaves the user with the Settings-window banner as
//     a second chance to install the update.
//   * Clicking "Update now" calls IUpdateService.CheckAndApplyAsync,
//     which restarts the process into the new version via Velopack.
internal sealed class UpdateAvailableNotifier : IHostedService
{
    private readonly IUpdateService _updateService;
    private readonly INotifier _notifier;
    private readonly ILogger<UpdateAvailableNotifier> _logger;

    public UpdateAvailableNotifier(
        IUpdateService updateService,
        INotifier notifier,
        ILogger<UpdateAvailableNotifier> logger)
    {
        _updateService = updateService;
        _notifier = notifier;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _updateService.UpdateAvailable += OnUpdateAvailable;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _updateService.UpdateAvailable -= OnUpdateAvailable;
        return Task.CompletedTask;
    }

    private void OnUpdateAvailable(string version)
    {
        // Fire-and-forget the async show — the event source doesn't await.
        _ = Task.Run(async () =>
        {
            try
            {
                var request = new ActionRequest(
                    Title: "Update available",
                    Message: $"AzureTray v{version} is ready to install. The download has finished — clicking Update now restarts into the new version.",
                    ActionLabel: "Update now")
                {
                    Severity = NotificationSeverity.Update,
                };

                var result = await _notifier.ShowAsync(request, CancellationToken.None).ConfigureAwait(false);

                if (result is ActionResult { ActionInvoked: true })
                {
                    _logger.LogInformation("User accepted update {Version}; applying.", version);
                    // Trigger the apply path; this returns "Restarting…"
                    // and the process is killed by Velopack a moment
                    // later. Don't await its return-string display —
                    // the UI is about to die anyway.
                    _ = _updateService.CheckAndApplyAsync();
                }
                else
                {
                    _logger.LogInformation(
                        "User dismissed update {Version} notification; Settings banner still surfaces the prompt.",
                        version);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to surface update-available notification for v{Version}.", version);
            }
        });
    }
}
