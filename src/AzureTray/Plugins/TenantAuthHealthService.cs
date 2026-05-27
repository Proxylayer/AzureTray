using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using AzureTray.Plugin.Contracts;
using AzureTray.Tenants;

namespace AzureTray.Plugins;

// Coordinator + background monitor for runtime token-renewal failures.
//
// Two detection feeds report in:
//   * Reactive — HostPluginHttpClient reports a failure when a plugin's next
//     call can't get a token, and a recovery when one succeeds.
//   * Proactive — this service's IHostedService loop silently re-probes every
//     ready tenant on a timer so idle tenants (no plugin traffic) are caught.
//
// State per tenant is a tiny machine guarded by its own lock so concurrent
// reports (multiple plugins on the same tenant) collapse to one popup. See
// ITenantAuthHealth for the user-visible contract.
public sealed class TenantAuthHealthService : ITenantAuthHealth, IHostedService, IDisposable
{
    // Cap on each silent re-probe — pure cache/broker-silent paths are fast;
    // anything slower is treated as "not a definitive auth failure" and left
    // alone (could be a transient network stall).
    private static readonly TimeSpan SilentProbeTimeout = TimeSpan.FromSeconds(20);

    private readonly ITenantStore _tenantStore;
    private readonly ICredentialFactory _credentials;
    private readonly ITenantReadinessTracker _tracker;
    private readonly IAzureCloudConfig _cloud;
    private readonly INotifier _notifier;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<TenantAuthHealthService> _logger;

    private readonly ConcurrentDictionary<string, TenantAuthEntry> _entries
        = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorLoop;

    public TenantAuthHealthService(
        ITenantStore tenantStore,
        ICredentialFactory credentials,
        ITenantReadinessTracker tracker,
        IAzureCloudConfig cloud,
        INotifier notifier,
        IOptions<AuthOptions> authOptions,
        ILogger<TenantAuthHealthService> logger)
    {
        _tenantStore = tenantStore;
        _credentials = credentials;
        _tracker = tracker;
        _cloud = cloud;
        _notifier = notifier;
        _authOptions = authOptions.Value;
        _logger = logger;
    }

    public event Action<string>? AuthStateChanged;

    public bool NeedsReauth(string tenantId)
        => !string.IsNullOrWhiteSpace(tenantId)
           && _entries.TryGetValue(tenantId, out var entry)
           && entry.Failed;

    public IReadOnlyCollection<string> FailedTenants
        => _entries.Where(kvp => kvp.Value.Failed)
                   .Select(kvp => kvp.Key)
                   .ToArray();

    public void ReportFailure(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;

        // Only previously-ready tenants. A tenant still being onboarded is the
        // startup probe's job; flagging it here would double-prompt.
        if (!_tracker.IsReady(tenantId)) return;

        var entry = _entries.GetOrAdd(tenantId, _ => new TenantAuthEntry());

        bool fireEvent;
        bool showPopup;
        CancellationTokenSource? popupCts = null;
        lock (entry.Gate)
        {
            // An interactive sign-in is already running — don't interfere.
            if (entry.SignInInProgress) return;

            fireEvent = !entry.Failed;
            entry.Failed = true;

            showPopup = !entry.NotificationActive;
            if (showPopup)
            {
                entry.NotificationActive = true;
                entry.Cts = popupCts = new CancellationTokenSource();
            }
        }

        if (fireEvent)
        {
            _logger.LogWarning(
                "Tenant {TenantId} token failed to renew; surfacing re-auth prompt.", tenantId);
            RaiseStateChanged(tenantId);
        }

        if (showPopup && popupCts is not null)
        {
            _ = ShowReauthPopupAsync(tenantId, entry, popupCts);
        }
    }

    public void ReportRecovered(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        if (!_entries.TryGetValue(tenantId, out var entry)) return;

        bool wasFailed;
        CancellationTokenSource? toCancel;
        lock (entry.Gate)
        {
            wasFailed = entry.Failed;
            entry.Failed = false;
            entry.NotificationActive = false;
            toCancel = entry.Cts;
            entry.Cts = null;
        }

        // Closes any open popup (NotificationService registers the token to
        // close the window). Done outside the lock. The popup CTS has no timer,
        // so we cancel without disposing — disposing here could race a late
        // token registration inside ShowAsync and throw ObjectDisposedException.
        if (toCancel is not null)
        {
            try { toCancel.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (wasFailed)
        {
            _logger.LogInformation("Tenant {TenantId} token recovered; clearing re-auth prompt.", tenantId);
            RaiseStateChanged(tenantId);
        }
    }

    public async Task<bool> TryResolveAsync(string tenantId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return false;

        var entry = _entries.GetOrAdd(tenantId, _ => new TenantAuthEntry());
        lock (entry.Gate)
        {
            if (entry.SignInInProgress) return false;
            entry.SignInInProgress = true;
        }

        try
        {
            await _credentials.SignInAsync(tenantId, cancellationToken).ConfigureAwait(false);
            ReportRecovered(tenantId);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Interactive sign-in failed for tenant {TenantId}.", tenantId);
            // Surface the failure so the click doesn't appear to do nothing.
            // The tenant stays in needs-reauth state and the popup re-raises on
            // the next detected failure.
            var tenant = _tenantStore.FindByTenantId(tenantId);
            var name = tenant?.DisplayName ?? tenantId;
            try
            {
                await _notifier.ShowAsync(
                    new InformationRequest(
                        $"Sign in to {name} failed",
                        "The sign-in didn't complete. AzureTray will prompt again the next time it detects the tenant needs re-authentication.")
                    {
                        Severity = NotificationSeverity.Error,
                        Details = new[] { new NotificationDetail("Tenant", tenantId) },
                    },
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception notifyEx)
            {
                _logger.LogWarning(notifyEx, "Failed to surface sign-in failure for tenant {TenantId}.", tenantId);
            }
            return false;
        }
        finally
        {
            lock (entry.Gate)
            {
                entry.SignInInProgress = false;
            }
        }
    }

    private async Task ShowReauthPopupAsync(string tenantId, TenantAuthEntry entry, CancellationTokenSource popupCts)
    {
        var tenant = _tenantStore.FindByTenantId(tenantId);
        var name = tenant?.DisplayName ?? tenantId;

        NotificationResult result;
        try
        {
            // ActionRequest never auto-dismisses. The linked token lets a
            // recovery elsewhere (monitor / Settings) close the popup.
            var request = new ActionRequest(
                Title: $"Sign-in expired — {name}",
                Message: $"AzureTray can no longer renew access to \"{name}\". Plugins for this tenant will keep failing until you sign in again.",
                ActionLabel: "Sign in")
            {
                Severity = NotificationSeverity.Warning,
                Details = new[] { new NotificationDetail("Tenant", tenantId) },
            };

            result = await _notifier.ShowAsync(request, popupCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to show re-auth popup for tenant {TenantId}.", tenantId);
            lock (entry.Gate) { entry.NotificationActive = false; }
            return;
        }

        bool stillFailed;
        lock (entry.Gate)
        {
            entry.NotificationActive = false;
            stillFailed = entry.Failed;
        }

        // Recovered while the popup was up (cts cancelled, or a plugin call /
        // monitor succeeded) — nothing to do.
        if (!stillFailed) return;

        if (result is ActionResult { ActionInvoked: true })
        {
            await TryResolveAsync(tenantId, CancellationToken.None).ConfigureAwait(false);
        }
        // Otherwise the user dismissed it: NotificationActive is now false, so
        // the next reported failure re-raises the popup.
    }

    private void RaiseStateChanged(string tenantId)
    {
        try
        {
            AuthStateChanged?.Invoke(tenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An AuthStateChanged subscriber threw for tenant {TenantId}.", tenantId);
        }
    }

    // ---- Background monitor (IHostedService) --------------------------------

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _monitorCts = new CancellationTokenSource();
        var token = _monitorCts.Token;
        _monitorLoop = Task.Run(() => MonitorLoopAsync(token), token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_monitorCts is null) return;
        try { await _monitorCts.CancelAsync().ConfigureAwait(false); } catch (ObjectDisposedException) { }
        if (_monitorLoop is not null)
        {
            try { await _monitorLoop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        _monitorCts.Dispose();
        _monitorCts = null;
    }

    public void Dispose()
    {
        // StopAsync normally disposes and nulls this; guard so container
        // disposal after a stop (or without one) is safe either way.
        _monitorCts?.Dispose();
        _monitorCts = null;
    }

    private async Task MonitorLoopAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(30, _authOptions.TokenMonitorIntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        // First tick fires after one interval, leaving startup sign-in to the
        // TenantReadinessProbe.
        while (await SafeWaitForTickAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            foreach (var ready in _tracker.ReadyTenants)
            {
                if (stoppingToken.IsCancellationRequested) return;

                var tenant = _tenantStore.FindByTenantId(ready.TenantId);
                if (tenant is { ProbeDisabled: true }) continue;

                await ProbeOnceAsync(ready.TenantId, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> SafeWaitForTickAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ProbeOnceAsync(string tenantId, CancellationToken stoppingToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(SilentProbeTimeout);

            var credential = _credentials.GetForTenant(tenantId);
            var ctx = new TokenRequestContext(new[] { _cloud.GraphScope });
            await credential.GetTokenAsync(ctx, cts.Token).ConfigureAwait(false);

            ReportRecovered(tenantId);
        }
        catch (AuthenticationRequiredException)
        {
            ReportFailure(tenantId);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host shutdown
        }
        catch (Exception ex)
        {
            // Network blip / timeout — not a definitive auth failure, leave the
            // tenant's state untouched.
            _logger.LogDebug(ex, "Background token re-probe for tenant {TenantId} was inconclusive.", tenantId);
        }
    }

    private sealed class TenantAuthEntry
    {
        public readonly object Gate = new();
        // Read outside the lock (NeedsReauth / FailedTenants) and written under
        // it — volatile keeps cross-thread reads from seeing a stale value.
        public volatile bool Failed;
        public bool NotificationActive;
        public bool SignInInProgress;
        public CancellationTokenSource? Cts;
    }
}
