using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;

namespace AzureTray.Plugin.PIM.Watchers;

// One watcher per tenant. Polls both Microsoft Graph (Entra ID PIM) and ARM
// (Azure RBAC PIM) for pending approvals. New approvals (not seen on the
// previous poll, regardless of source) surface as interactive notifications;
// the user's decision is routed back to the source that produced the approval.
//
// Acted-upon approvals fall out of the seen-set automatically once they no
// longer appear in either feed.
internal sealed class PendingApprovalWatcher
{
    private static readonly string[] ApproveOrRejectChoices = { "Approve", "Reject" };

    private readonly IGraphPimClient _graph;
    private readonly IArmPimClient _arm;
    private readonly IPluginContext _context;
    private readonly PluginTenant _tenant;
    private readonly TimeSpan _interval;
    private readonly Func<IReadOnlySet<string>>? _relevantSubscriptions;
    private readonly HashSet<string> _seenKeys = new(StringComparer.OrdinalIgnoreCase);

    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<UnifiedPendingApproval> _lastSnapshot = Array.Empty<UnifiedPendingApproval>();

    public PendingApprovalWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval,
        Func<IReadOnlySet<string>>? relevantSubscriptions = null)
    {
        _graph = graph;
        _arm = arm;
        _context = context;
        _tenant = tenant;
        _interval = interval;
        _relevantSubscriptions = relevantSubscriptions;
    }

    // Raised at the start and end of each PollAsync so the host can spin a
    // refresh indicator and rebuild the menu. May fire on a thread-pool thread;
    // subscribers must marshal as needed.
    public event Action? PollStarted;
    public event Action? PollCompleted;

    public string TenantId => _tenant.TenantId;
    public string TenantDisplayName => _tenant.DisplayName;

    public bool IsPolling { get; private set; }

    // Snapshot of the most recent poll. Returns the same instance until the
    // next PollAsync completes, so callers don't need to copy.
    public IReadOnlyList<UnifiedPendingApproval> CurrentApprovals => _lastSnapshot;

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
                    "Pending-approval poll failed for tenant {TenantId}; will retry next interval.",
                    _tenant.TenantId);
            }

            try { await Task.Delay(_interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task PollAsync(CancellationToken cancellationToken)
    {
        IsPolling = true;
        PollStarted?.Invoke();
        try
        {
            var graphTask = FetchGraphAsync(cancellationToken);
            var armTask = FetchArmAsync(cancellationToken);

            var graphPending = await graphTask.ConfigureAwait(false);
            var armPending = await armTask.ConfigureAwait(false);

            var all = graphPending.Concat(armPending).ToArray();
            _lastSnapshot = all;

            var currentKeys = new HashSet<string>(all.Select(a => a.DedupKey), StringComparer.OrdinalIgnoreCase);

            foreach (var approval in all)
            {
                if (_seenKeys.Add(approval.DedupKey))
                {
                    _ = HandleNewApprovalAsync(approval, cancellationToken);
                }
            }

            _seenKeys.IntersectWith(currentKeys);
        }
        finally
        {
            IsPolling = false;
            PollCompleted?.Invoke();
        }
    }

    private async Task<List<UnifiedPendingApproval>> FetchGraphAsync(CancellationToken ct)
    {
        try
        {
            var requests = await _graph.ListPendingApprovalsAsync(_tenant.TenantId, ct).ConfigureAwait(false);
            return requests
                .Where(r => !string.IsNullOrWhiteSpace(r.ApprovalId))
                .Select(r => new UnifiedPendingApproval(
                    Source: PimSource.EntraId,
                    ApprovalId: r.ApprovalId!,
                    PrincipalDisplay: r.Principal?.DisplayName ?? r.Principal?.UserPrincipalName ?? "(unknown user)",
                    RoleDisplay: r.RoleDefinition?.DisplayName ?? "(unknown role)",
                    ScopeDisplay: "Entra ID directory",
                    ArmScope: null))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "Graph pending-approval fetch failed for tenant {TenantId}; continuing with ARM only.",
                _tenant.TenantId);
            return new();
        }
    }

    private async Task<List<UnifiedPendingApproval>> FetchArmAsync(CancellationToken ct)
    {
        try
        {
            var subs = await _arm.ListSubscriptionsAsync(_tenant.TenantId, ct).ConfigureAwait(false);
            if (subs.Count == 0) return new();

            var relevant = _relevantSubscriptions?.Invoke();
            var scopes = subs
                .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId))
                // First poll, before eligibility has populated, the relevant set
                // is empty — fall back to scanning every subscription so we don't
                // miss approvals until the slower eligibility tick lands.
                .Where(s => relevant is null || relevant.Count == 0 || relevant.Contains(s.SubscriptionId!))
                .Select(s => $"/subscriptions/{s.SubscriptionId}")
                .ToList();
            if (scopes.Count == 0) return new();

            var requests = await _arm.ListPendingApprovalsAsync(_tenant.TenantId, scopes, ct).ConfigureAwait(false);

            return requests
                .Where(r => !string.IsNullOrWhiteSpace(r.Properties?.ApprovalId))
                .Select(r => new UnifiedPendingApproval(
                    Source: PimSource.AzureRbac,
                    ApprovalId: r.Properties!.ApprovalId!,
                    PrincipalDisplay: r.Properties.ExpandedProperties?.Principal?.DisplayName ?? "(unknown user)",
                    RoleDisplay: r.Properties.ExpandedProperties?.RoleDefinition?.DisplayName ?? "(unknown role)",
                    ScopeDisplay: r.Properties.ExpandedProperties?.Scope?.DisplayName ?? r.Properties.Scope ?? "(unknown scope)",
                    ArmScope: r.Properties.Scope))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(
                ex,
                "ARM pending-approval fetch failed for tenant {TenantId}; continuing with Graph only.",
                _tenant.TenantId);
            return new();
        }
    }

    internal async Task HandleNewApprovalAsync(UnifiedPendingApproval approval, CancellationToken cancellationToken)
    {
        try
        {
            var choice = await _context.Notifier.ShowAsync(
                new ChoiceRequest(
                    Title: $"PIM approval — {_tenant.DisplayName}",
                    Message: $"{approval.PrincipalDisplay} is requesting {approval.RoleDisplay} on {approval.ScopeDisplay}.",
                    Choices: ApproveOrRejectChoices),
                cancellationToken).ConfigureAwait(false);

            if (choice is not ChoiceResult { SelectedChoice: { } picked })
            {
                _context.Logger.LogDebug(
                    "{Source} approval {ApprovalId} dismissed without decision on tenant {TenantId}.",
                    approval.Source, approval.ApprovalId, _tenant.TenantId);
                return;
            }

            var decision = picked switch
            {
                "Approve" => ApprovalDecision.Approve,
                "Reject" => ApprovalDecision.Deny,
                _ => (ApprovalDecision?)null,
            };
            if (decision is null) return;

            var justification = await _context.Notifier.ShowAsync(
                new TextInputRequest(
                    Title: $"Justification — {decision}",
                    Message: $"Why are you {(decision == ApprovalDecision.Approve ? "approving" : "rejecting")} {approval.RoleDisplay}?",
                    Placeholder: "Required"),
                cancellationToken).ConfigureAwait(false);

            if (justification is not TextInputResult { Text: { } justText } || string.IsNullOrWhiteSpace(justText))
            {
                _context.Logger.LogInformation(
                    "{Source} approval {ApprovalId} on tenant {TenantId}: user dismissed at justification prompt; no action taken.",
                    approval.Source, approval.ApprovalId, _tenant.TenantId);
                return;
            }

            switch (approval.Source)
            {
                case PimSource.EntraId:
                    await _graph.ReviewAsync(
                        _tenant.TenantId,
                        approval.ApprovalId,
                        decision.Value,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PimSource.AzureRbac:
                    if (string.IsNullOrWhiteSpace(approval.ArmScope))
                    {
                        _context.Logger.LogError(
                            "ARM approval {ApprovalId} on tenant {TenantId} has no scope; cannot review.",
                            approval.ApprovalId, _tenant.TenantId);
                        return;
                    }
                    await _arm.ReviewAsync(
                        _tenant.TenantId,
                        approval.ArmScope,
                        approval.ApprovalId,
                        decision.Value,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _context.Logger.LogError(
                ex,
                "Failed to handle {Source} approval {ApprovalId} on tenant {TenantId}.",
                approval.Source, approval.ApprovalId, _tenant.TenantId);
        }
    }
}
