using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Notifications;

// Interactive notifications rendered as topmost, frameless WPF popups in the
// bottom-right of the working area. Multiple notifications stack tightly —
// each one is anchored above the one below it with a small StackSpacing gap,
// sized to that window's actual rendered height (not a fixed slot). When a
// notification opens, closes, or grows, the stack reflows so the gap between
// toasts stays small rather than leaving popup-sized holes for shorter content.
public sealed class NotificationService : INotifier
{
    private const double WindowWidth = 360;
    private const double EdgeMargin = 16;
    private const double StackSpacing = 8;
    // Estimate used before a window's ActualHeight is known (its first
    // ContentRendered hasn't fired yet). Reposition fixes it up as soon as
    // real heights arrive — this only avoids a flash at the wrong Y.
    private const double FallbackHeight = 110;

    private readonly ILogger<NotificationService> _logger;
    private readonly Lock _stackGate = new();
    // Bottom-to-top: index 0 is the bottommost visible notification.
    private readonly List<NotificationWindow> _stack = new();

    public NotificationService(ILogger<NotificationService> logger)
    {
        _logger = logger;
    }

    public Task<NotificationResult> ShowAsync(NotificationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromResult<NotificationResult>(new DismissedResult());
        }

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            _logger.LogWarning(
                "Notification suppressed for {Title}: WPF Application is not running yet.",
                request.Title);
            return Task.FromResult<NotificationResult>(new DismissedResult());
        }

        var tcs = new TaskCompletionSource<NotificationResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        dispatcher.InvokeAsync(() =>
        {
            try
            {
                var vm = new NotificationViewModel(request, tcs);
                var window = new NotificationWindow { DataContext = vm };

                AddToStack(window);

                // Re-pack the stack as each window's actual height becomes
                // known (ContentRendered) or changes (Details expanded →
                // SizeChanged). Without this the stack would either reserve a
                // generic max-height slot per toast (popup-sized gaps for
                // short content) or place subsequent toasts before their
                // neighbours' heights settled.
                window.ContentRendered += (_, _) => Reposition();
                window.SizeChanged += (_, _) => Reposition();

                window.Closed += (_, _) =>
                {
                    RemoveFromStack(window);
                    vm.OnWindowClosed();
                };

                // Clicking Submit / Yes / No on the VM resolves the TCS but
                // doesn't close the window by itself — close it here so the
                // dialog goes away as soon as the user makes a choice.
                vm.Completed += () => dispatcher.InvokeAsync(() =>
                {
                    if (window.IsVisible) window.Close();
                });

                window.Show();

                // Passive toasts auto-dismiss after a few seconds. Interactive
                // requests (Yes/No, Choice, TextInput) wait for the user.
                if (request is InformationRequest)
                {
                    var timer = new System.Windows.Threading.DispatcherTimer(
                        TimeSpan.FromSeconds(4),
                        System.Windows.Threading.DispatcherPriority.Normal,
                        (_, _) => { if (window.IsVisible) window.Close(); },
                        dispatcher);
                    timer.Start();
                    window.Closed += (_, _) => timer.Stop();
                }

                if (cancellationToken.CanBeCanceled)
                {
                    cancellationToken.Register(() =>
                    {
                        dispatcher.InvokeAsync(() =>
                        {
                            if (window.IsVisible) window.Close();
                        });
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to display notification {Title}.", request.Title);
                tcs.TrySetResult(new DismissedResult());
            }
        });

        return tcs.Task;
    }

    private void AddToStack(NotificationWindow window)
    {
        lock (_stackGate) { _stack.Add(window); }
        Reposition();
    }

    private void RemoveFromStack(NotificationWindow window)
    {
        lock (_stackGate) { _stack.Remove(window); }
        Reposition();
    }

    // Anchors every open notification bottom-right, stacking upward with a
    // StackSpacing gap, using each window's ActualHeight (with a Fallback
    // when not yet rendered). Cheap enough to run on every add / remove /
    // render / resize without throttling.
    private void Reposition()
    {
        NotificationWindow[] snap;
        lock (_stackGate) { snap = _stack.ToArray(); }
        if (snap.Length == 0) return;

        var wa = SystemParameters.WorkArea;
        double cumulativeAboveBottom = 0;
        foreach (var w in snap)
        {
            var h = w.ActualHeight > 0 ? w.ActualHeight : FallbackHeight;
            w.Width = WindowWidth;
            w.Left = wa.Right - WindowWidth - EdgeMargin;
            var bottom = wa.Bottom - EdgeMargin - cumulativeAboveBottom;
            w.Top = Math.Max(wa.Top + EdgeMargin, bottom - h);
            cumulativeAboveBottom += h + StackSpacing;
        }
    }
}
