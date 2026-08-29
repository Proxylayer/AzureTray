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
    private readonly Func<IReadOnlySet<string>>? _relevantManagementGroupScopes;
    private readonly HashSet<string> _seenKeys = new(StringComparer.OrdinalIgnoreCase);

    // Once-then-quiet logging for the two per-poll fetches: an expired token
    // makes both fail identically every interval, and only the transitions
    // (fail once, recover once) carry information. See FetchFailureGate.
    private readonly FetchFailureGate _graphFetchFailures = new();
    private readonly FetchFailureGate _armFetchFailures = new();

    private Task? _loopTask;
    private CancellationTokenSource? _cts;
    private IReadOnlyList<UnifiedPendingApproval> _lastSnapshot = Array.Empty<UnifiedPendingApproval>();

    // Cached lazily on first poll. The signed-in user's objectId in this tenant
    // is stable for the lifetime of the install, so a single Graph /me call
    // per watcher is enough. Null means "not resolved yet" OR "resolved but
    // Graph returned nothing" — both cases fall through to "don't filter",
    // i.e. the original behavior of surfacing every approval.
    private string? _signedInUserId;
    private bool _signedInUserIdResolved;

    public PendingApprovalWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval,
        Func<IReadOnlySet<string>>? relevantSubscriptions = null,
        Func<IReadOnlySet<string>>? relevantManagementGroupScopes = null)
    {
        _graph = graph;
        _arm = arm;
        _context = context;
        _tenant = tenant;
        _interval = interval;
        _relevantSubscriptions = relevantSubscriptions;
        _relevantManagementGroupScopes = relevantManagementGroupScopes;
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
            await EnsureSignedInUserIdAsync(cancellationToken).ConfigureAwait(false);

            var graphTask = FetchGraphAsync(cancellationToken);
            var armTask = FetchArmAsync(cancellationToken);

            var graphPending = await graphTask.ConfigureAwait(false);
            var armPending = await armTask.ConfigureAwait(false);

            // Drop self-authored requests before they ever enter the snapshot
            // or the seen-set — surfacing "approve your own request" is
            // confusing and the PIM policy won't accept a self-review anyway.
            // When _signedInUserId is null (Graph /me failed or hasn't been
            // resolved yet) we fall through to the legacy behaviour of
            // showing everything, so a transient Graph hiccup never silently
            // hides approvals from other requestors.
            // DistinctBy: the ARM fan-out now covers management-group scopes as
            // well as subscriptions, and a request scoped at an MG comes back
            // from both the MG query and every descendant subscription query.
            // Collapse on the dedup key here so a cross-scope duplicate never
            // reaches the snapshot (menu) or the notify loop below.
            var all = graphPending.Concat(armPending)
                .Where(a => !IsSelfAuthored(a))
                .DistinctBy(a => a.DedupKey, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            _lastSnapshot = all;

            var currentKeys = new HashSet<string>(all.Select(a => a.DedupKey), StringComparer.OrdinalIgnoreCase);

            foreach (var approval in all)
            {
                if (_seenKeys.Add(approval.DedupKey))
                {
                    // The success path used to be silent, which made "the popup
                    // never appeared" indistinguishable from "the feed never saw
                    // the request" in the log. One Information line per newly
                    // surfaced approval.
                    _context.Logger.LogInformation(
                        "New {Source} pending approval {ApprovalId}: {Requestor} ({RequestorId}) requesting {Role} at {Scope} (tenant {TenantId}).",
                        approval.Source,
                        approval.ApprovalId,
                        approval.PrincipalDisplay,
                        approval.RequestorPrincipalId ?? "(unknown id)",
                        approval.RoleDisplay,
                        approval.ArmScope ?? approval.ScopeDisplay,
                        _tenant.TenantId);
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

    private async Task EnsureSignedInUserIdAsync(CancellationToken ct)
    {
        if (_signedInUserIdResolved) return;
        try
        {
            _signedInUserId = await _graph.GetSignedInUserIdAsync(ct).ConfigureAwait(false);
            _signedInUserIdResolved = true;
            if (string.IsNullOrWhiteSpace(_signedInUserId))
            {
                _context.Logger.LogDebug(
                    "PIM self-approval filter disabled for tenant {TenantId}: Graph /me returned no id.",
                    _tenant.TenantId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            // Leave _signedInUserIdResolved=false so the next poll retries.
            // A persistent failure just keeps the legacy "show everything"
            // behaviour, which is the safer default.
            _context.Logger.LogDebug(
                ex,
                "PIM self-approval filter could not resolve signed-in user for tenant {TenantId}; will retry.",
                _tenant.TenantId);
        }
    }

    private bool IsSelfAuthored(UnifiedPendingApproval approval)
    {
        if (string.IsNullOrWhiteSpace(_signedInUserId)) return false;
        if (string.IsNullOrWhiteSpace(approval.RequestorPrincipalId)) return false;
        return string.Equals(approval.RequestorPrincipalId, _signedInUserId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<List<UnifiedPendingApproval>> FetchGraphAsync(CancellationToken ct)
    {
        try
        {
            var requests = await _graph.ListPendingApprovalsAsync(ct).ConfigureAwait(false);
            LogFetchRecovered(_graphFetchFailures, "Graph");
            return requests
                .Where(r => !string.IsNullOrWhiteSpace(r.ApprovalId))
                .Select(r => new UnifiedPendingApproval(
                    Source: PimSource.EntraId,
                    ApprovalId: r.ApprovalId!,
                    PrincipalDisplay: r.Principal?.DisplayName ?? r.Principal?.UserPrincipalName ?? "(unknown user)",
                    RoleDisplay: r.RoleDefinition?.DisplayName ?? "(unknown role)",
                    ScopeDisplay: "Entra ID directory",
                    ArmScope: null,
                    RequestorPrincipalId: r.Principal?.Id ?? r.PrincipalId,
                    // The schedule request's own justification — the reason the
                    // requestor typed. Not EntraApprovalStep.Justification,
                    // which is an approver's decision comment.
                    RequestorJustification: r.Justification))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            LogFetchFailure(_graphFetchFailures, ex, "Graph", "ARM");
            return new();
        }
    }

    private async Task<List<UnifiedPendingApproval>> FetchArmAsync(CancellationToken ct)
    {
        try
        {
            var subs = await _arm.ListSubscriptionsAsync(ct).ConfigureAwait(false);

            var relevant = _relevantSubscriptions?.Invoke();
            var scopes = subs
                .Where(s => !string.IsNullOrWhiteSpace(s.SubscriptionId))
                // First poll, before eligibility has populated, the relevant set
                // is empty — fall back to scanning every subscription so we don't
                // miss approvals until the slower eligibility tick lands.
                .Where(s => relevant is null || relevant.Count == 0 || relevant.Contains(s.SubscriptionId!))
                .Select(s => $"/subscriptions/{s.SubscriptionId}")
                .ToList();

            // Management-group scopes where the user holds PIM eligibility.
            // asApprover() at a subscription scope never returns requests made
            // at a management group above it, so without these an MG-scoped
            // activation request from another user is structurally invisible.
            // No eligibility-derived MG scopes → no extra requests (identical
            // behavior to the subscription-only fan-out).
            var mgScopes = _relevantManagementGroupScopes?.Invoke();
            if (mgScopes is { Count: > 0 })
            {
                scopes.AddRange(mgScopes.Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            if (scopes.Count == 0)
            {
                // A poll with nothing to scan is still a successful poll —
                // the subscription listing worked.
                LogFetchRecovered(_armFetchFailures, "ARM");
                return new();
            }

            var requests = await _arm.ListPendingApprovalsAsync(scopes, ct).ConfigureAwait(false);
            LogFetchRecovered(_armFetchFailures, "ARM");

            return requests
                .Where(r => !string.IsNullOrWhiteSpace(r.Properties?.ApprovalId))
                .Select(r => new UnifiedPendingApproval(
                    Source: PimSource.AzureRbac,
                    ApprovalId: ParseArmApprovalId(r.Properties!.ApprovalId!),
                    PrincipalDisplay: r.Properties.ExpandedProperties?.Principal?.DisplayName ?? "(unknown user)",
                    RoleDisplay: r.Properties.ExpandedProperties?.RoleDefinition?.DisplayName ?? "(unknown role)",
                    ScopeDisplay: r.Properties.ExpandedProperties?.Scope?.DisplayName ?? r.Properties.Scope ?? "(unknown scope)",
                    ArmScope: r.Properties.Scope,
                    RequestorPrincipalId: r.Properties.ExpandedProperties?.Principal?.Id ?? r.Properties.PrincipalId,
                    // The schedule request's own justification, not
                    // ArmApprovalStageProperties.Justification (the approver's
                    // decision comment). ARM documents this as nullable.
                    RequestorJustification: r.Properties.Justification))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            LogFetchFailure(_armFetchFailures, ex, "ARM", "Graph");
            return new();
        }
    }

    private void LogFetchRecovered(FetchFailureGate gate, string source)
    {
        if (gate.RecordSuccess())
        {
            _context.Logger.LogInformation(
                "{Source} pending-approval fetch recovered for tenant {TenantId}.",
                source, _tenant.TenantId);
        }
    }

    private void LogFetchFailure(FetchFailureGate gate, Exception ex, string source, string continuingWith)
    {
        // Azure.Identity's AuthenticationRequiredException means "the silent
        // token path can't recover; interactive sign-in needed". The host's
        // TenantAuthHealthService already logs that transition once at Warning
        // and raises the re-auth prompt, so this watcher stays at Debug even
        // for the FIRST occurrence. The plugin runs in its own load context
        // and deliberately doesn't reference Azure.Identity (the host's copy
        // would be a different assembly identity anyway), so the type is
        // matched by full name.
        var knownCondition = ex.GetType().FullName == "Azure.Identity.AuthenticationRequiredException";

        switch (gate.RecordFailure(knownCondition))
        {
            case FetchFailureGate.FailureLog.WarnWithException:
                _context.Logger.LogWarning(
                    ex,
                    "{Source} pending-approval fetch failed for tenant {TenantId}; continuing with {ContinuingWith} only.",
                    source, _tenant.TenantId, continuingWith);
                break;

            case FetchFailureGate.FailureLog.DebugOneLine:
                _context.Logger.LogDebug(
                    "{Source} pending-approval fetch failing for tenant {TenantId} ({ExceptionType}: {ExceptionMessage}); continuing with {ContinuingWith} only.",
                    source, _tenant.TenantId, ex.GetType().Name, ex.Message, continuingWith);
                break;
        }
    }

    internal async Task HandleNewApprovalAsync(UnifiedPendingApproval approval, CancellationToken cancellationToken)
    {
        try
        {
            // The requestor's reason belongs in Message, not behind the Details
            // expander — an approver shouldn't have to click to see why they're
            // being asked. Details only carries the reason when the visible
            // copy had to be truncated.
            var reason = ApprovalReason.From(approval.RequestorJustification);

            var choice = await _context.Notifier.ShowAsync(
                new ChoiceRequest(
                    Title: $"PIM approval — {_tenant.DisplayName}",
                    Message: $"{approval.PrincipalDisplay} is requesting {approval.RoleDisplay} on {approval.ScopeDisplay}.\n\n{reason.MessageLine}",
                    Choices: ApproveOrRejectChoices)
                {
                    Details = reason.ClampedFullText is { } fullReason
                        ? new[] { new NotificationDetail("Reason", fullReason) }
                        : null,
                },
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
                        approval.ApprovalId,
                        decision.Value,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    break;

                case PimSource.AzureRbac:
                    await _arm.ReviewAsync(
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

    // ARM returns approvalId as a scope-relative resource path such as
    // "/providers/Microsoft.Authorization/roleAssignmentApprovals/{guid}".
    // Extract the trailing GUID segment so URL construction in ReviewAsync
    // doesn't double-up the provider path.
    private static string ParseArmApprovalId(string approvalId)
    {
        var last = approvalId.LastIndexOf('/');
        return last >= 0 && last < approvalId.Length - 1
            ? approvalId[(last + 1)..]
            : approvalId;
    }
}
