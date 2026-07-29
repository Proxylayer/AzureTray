using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;

namespace AzureTray.Plugin.PIM.Watchers;

// One watcher per tenant. Polls the activation requests the signed-in user
// submitted that went to an approver (PendingActivationStore), on the same fast
// cadence as PendingApprovalWatcher. This is the mirror image of that watcher:
// it is about the user's OWN requests, not requests they must action.
//
// When a request turns Provisioned the role's claims exist server-side but the
// cached access token predates them, so the host is asked to force-refresh the
// tenant's tokens before the eligible-role snapshot is re-read. Without that the
// new role only takes effect when the cached token rolls over (~60-90 minutes).
internal sealed class PendingActivationWatcher
{
    // Requests nobody ever decides are dropped rather than polled forever.
    private static readonly TimeSpan MaxTrackingAge = TimeSpan.FromHours(24);

    private readonly IGraphPimClient _graph;
    private readonly IArmPimClient _arm;
    private readonly IPluginContext _context;
    private readonly PluginTenant _tenant;
    private readonly TimeSpan _interval;
    private readonly PendingActivationStore _store;
    private readonly Func<CancellationToken, Task> _refreshActiveRoles;

    private Task? _loopTask;
    private CancellationTokenSource? _cts;

    public PendingActivationWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval,
        PendingActivationStore store,
        Func<CancellationToken, Task> refreshActiveRoles)
    {
        _graph = graph;
        _arm = arm;
        _context = context;
        _tenant = tenant;
        _interval = interval;
        _store = store;
        _refreshActiveRoles = refreshActiveRoles;
    }

    // Raised after an approved activation has been reflected in the token and
    // the eligible-role snapshot, so the host can rebuild the tray menu.
    public event Action? ActivationProvisioned;

    public string TenantId => _tenant.TenantId;

    // Activations currently waiting on an approver for this tenant.
    public int TrackedCount => _store.Current.Count;

    public void Start(CancellationToken stopToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        _loopTask = Task.Run(() => LoopAsync(_cts.Token), _cts.Token);
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts?.Dispose();
        _cts = null;
        _loopTask = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _context.Logger.LogWarning(
                    ex,
                    "Pending-activation poll failed for tenant {TenantId}; will retry next interval.",
                    _tenant.TenantId);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        _store.DropOlderThan(MaxTrackingAge);

        var tracked = _store.Current;
        if (tracked.Count == 0) return;

        foreach (var request in tracked)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var status = await FetchStatusAsync(request, cancellationToken).ConfigureAwait(false);
            if (status is null) continue;

            if (ActivationStatus.IsProvisioned(status))
            {
                _store.StopTracking(request.RequestId);
                await OnProvisionedAsync(request, cancellationToken).ConfigureAwait(false);
            }
            else if (ActivationStatus.IsTerminalFailure(status))
            {
                _store.StopTracking(request.RequestId);
                await OnRefusedAsync(request, status, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                _context.Logger.LogDebug(
                    "PIM activation {RequestId} ({RoleName}) on tenant {TenantId} is still {Status}.",
                    request.RequestId, request.RoleName, _tenant.TenantId, status);
            }
        }
    }

    // Null means "couldn't tell this cycle" — a transient failure leaves the
    // request tracked so the next poll retries, up to MaxTrackingAge.
    private async Task<string?> FetchStatusAsync(PendingActivationRequest request, CancellationToken ct)
    {
        try
        {
            return request.Source switch
            {
                PimSource.EntraId =>
                    await _graph.GetActivationStatusAsync(request.RequestId, ct).ConfigureAwait(false),
                PimSource.AzureRbac when !string.IsNullOrWhiteSpace(request.ArmScope) =>
                    await _arm.GetActivationStatusAsync(request.ArmScope!, request.RequestId, ct).ConfigureAwait(false),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return null; }
        catch (Exception ex)
        {
            _context.Logger.LogDebug(
                ex,
                "Could not read status of PIM activation {RequestId} on tenant {TenantId}; will retry.",
                request.RequestId, _tenant.TenantId);
            return null;
        }
    }

    private async Task OnProvisionedAsync(PendingActivationRequest request, CancellationToken ct)
    {
        _context.Logger.LogInformation(
            "PIM activation {RequestId} ({RoleName} on {Scope}) was approved for tenant {TenantId}; refreshing token and roles.",
            request.RequestId, request.RoleName, request.ScopeDisplay, _tenant.TenantId);

        // Old hosts return false from the contract's default implementation;
        // that is not an error, the claims just arrive with the next token.
        var refreshed = await _context.RefreshTokenAsync(_tenant.TenantId, ct).ConfigureAwait(false);
        if (!refreshed)
        {
            _context.Logger.LogDebug(
                "Host did not force-refresh the token for tenant {TenantId}; the new role's claims will apply once the cached token rolls over.",
                _tenant.TenantId);
        }

        // Re-read eligibility/actives so the row flips to "✓ active" with its
        // remaining time instead of waiting for the slow eligibility tick.
        try
        {
            await _refreshActiveRoles(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex,
                "Active-role refresh after approval of {RequestId} failed for tenant {TenantId}.",
                request.RequestId, _tenant.TenantId);
        }

        _ = _context.Notifier.ShowAsync(
            new InformationRequest(
                Title: $"Approved: {request.RoleName}",
                Message: $"on {request.ScopeDisplay} — the role is active now.")
            {
                Severity = NotificationSeverity.Success,
            },
            CancellationToken.None);

        ActivationProvisioned?.Invoke();
    }

    private async Task OnRefusedAsync(PendingActivationRequest request, string status, CancellationToken ct)
    {
        _context.Logger.LogInformation(
            "PIM activation {RequestId} ({RoleName}) on tenant {TenantId} ended as {Status}; no longer tracking.",
            request.RequestId, request.RoleName, _tenant.TenantId, status);

        await _context.Notifier.ShowAsync(
            new InformationRequest(
                Title: $"Not activated: {request.RoleName}",
                Message: $"the request for {request.ScopeDisplay} ended as {status}.")
            {
                Severity = NotificationSeverity.Warning,
            },
            ct).ConfigureAwait(false);
    }
}
