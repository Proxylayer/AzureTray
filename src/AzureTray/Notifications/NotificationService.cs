using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Notifications;

// Interactive notifications rendered as topmost, frameless WPF popups in the
// bottom-right of the working area. Multiple notifications stack — each new
// one is placed above the existing stack. When a notification closes, its slot
// is released for the *next* arrival (sliding the others is not attempted —
// the user said "stack them", not "reflow them").
public sealed class NotificationService : INotifier
{
    private const double WindowWidth = 360;
    private const double EdgeMargin = 16;
    private const double StackSlotHeight = 220;
    private const double StackSpacing = 8;

    private readonly ILogger<NotificationService> _logger;
    private readonly Lock _slotsGate = new();
    private readonly SortedSet<int> _occupiedSlots = new();

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

                var slot = AcquireSlot();
                PositionWindow(window, slot);

                window.Closed += (_, _) =>
                {
                    ReleaseSlot(slot);
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

    private int AcquireSlot()
    {
        lock (_slotsGate)
        {
            // Smallest free index from 0 upward — recycles freed slots.
            var slot = 0;
            foreach (var occupied in _occupiedSlots)
            {
                if (occupied != slot) break;
                slot++;
            }
            _occupiedSlots.Add(slot);
            return slot;
        }
    }

    private void ReleaseSlot(int slot)
    {
        lock (_slotsGate)
        {
            _occupiedSlots.Remove(slot);
        }
    }

    private static void PositionWindow(NotificationWindow window, int slot)
    {
        var workArea = SystemParameters.WorkArea;
        window.Width = WindowWidth;
        window.Left = workArea.Right - WindowWidth - EdgeMargin;
        window.Top = workArea.Bottom - (slot + 1) * (StackSlotHeight + StackSpacing) - EdgeMargin;
    }
}
