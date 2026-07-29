using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Extensions;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Notifications;

// Toasts for plugin updates, shaped like the host's own UpdateAvailableNotifier:
//   * NotificationSeverity.Update → blue accent + upload glyph.
//   * ActionRequest, so it never auto-dismisses; closing it leaves the
//     Settings banner as the second chance.
//   * ALWAYS aggregated — one notification for every plugin with an update,
//     never one per plugin. A user with five stale plugins gets one toast.
//
// Opening Settings is TrayIcon's job (it owns the window instance), so the
// action raises OpenSettingsRequested rather than reaching into the UI here.
public sealed class PluginUpdateNotifier
{
    private readonly INotifier _notifier;
    private readonly ILogger<PluginUpdateNotifier> _logger;

    public PluginUpdateNotifier(INotifier notifier, ILogger<PluginUpdateNotifier> logger)
    {
        _notifier = notifier;
        _logger = logger;
    }

    // Raised when the user clicks the toast's action. TrayIcon subscribes and
    // shows the Settings window on the dispatcher thread.
    public event Action? OpenSettingsRequested;

    // Fire-and-forget, like the host's own update toast: an ActionRequest
    // stays up until the user acts on it, and neither the poll loop nor the
    // auto-updater may block for that long.
    public void ShowUpdatesAvailable(IReadOnlyList<PluginUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);
        if (updates.Count == 0) return;

        _ = Task.Run(() => ShowUpdatesAvailableAsync(updates));
    }

    public void ShowUpdatesApplied(IReadOnlyList<PluginUpdate> applied)
    {
        ArgumentNullException.ThrowIfNull(applied);
        if (applied.Count == 0) return;

        _ = Task.Run(() => ShowUpdatesAppliedAsync(applied));
    }

    private async Task ShowUpdatesAvailableAsync(IReadOnlyList<PluginUpdate> updates)
    {
        try
        {
            var title = updates.Count == 1
                ? "Plugin update available"
                : $"{updates.Count} plugin updates available";

            var body = string.Join("\n", updates.Select(u => "  • " + u.SummaryLine));
            var request = new ActionRequest(
                Title: title,
                Message:
                    $"nuget.org has a newer version of {(updates.Count == 1 ? "an installed plugin" : "installed plugins")}:\n\n{body}\n\n" +
                    "Open Settings → Plugins and click Update on the plugin's row to install it.",
                ActionLabel: "Open Settings")
            {
                Severity = NotificationSeverity.Update,
                Details = updates
                    .Select(u => new NotificationDetail(u.PackageId, $"{u.InstalledVersion} → {u.LatestVersion}"))
                    .ToArray(),
            };

            var result = await _notifier.ShowAsync(request, CancellationToken.None).ConfigureAwait(false);
            if (result is ActionResult { ActionInvoked: true })
            {
                OpenSettingsRequested?.Invoke();
            }
            else
            {
                _logger.LogInformation(
                    "User dismissed the plugin-update notification for {Count} plugin(s); the Settings banner still surfaces it.",
                    updates.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to surface the plugin-update notification.");
        }
    }

    // Reports what auto-update actually did. Aggregated the same way, and
    // non-actionable — the work is already done.
    private async Task ShowUpdatesAppliedAsync(IReadOnlyList<PluginUpdate> applied)
    {
        try
        {
            var body = string.Join("\n", applied.Select(u => "  • " + u.SummaryLine));
            var request = new InformationRequest(
                Title: applied.Count == 1 ? "Plugin updated" : $"{applied.Count} plugins updated",
                Message: $"Auto-update installed:\n\n{body}\n\nThe new version is already loaded — no restart needed.")
            {
                Severity = NotificationSeverity.Success,
                Details = applied
                    .Select(u => new NotificationDetail(u.PackageId, $"{u.InstalledVersion} → {u.LatestVersion}"))
                    .ToArray(),
            };

            await _notifier.ShowAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to surface the plugin auto-update report.");
        }
    }
}
