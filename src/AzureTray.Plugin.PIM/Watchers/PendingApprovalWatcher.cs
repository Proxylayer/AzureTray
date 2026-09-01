using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;

namespace AzureTray.Plugin.PIM.Watchers;

// One watcher per tenant. Polls Microsoft Graph (Entra ID directory-role PIM),
// ARM (Azure RBAC PIM) and Graph again (PIM for Groups) for pending approvals.
// New approvals (not seen on the previous poll, regardless of source) surface as
// interactive notifications; the user's decision is routed back to the source
// that produced the approval.
//
// Acted-upon approvals fall out of the seen-set automatically once they no
// longer appear in any feed.
internal sealed class PendingApprovalWatcher : PollingWatcher
{
    private static readonly string[] ApproveOrRejectChoices = { "Approve", "Reject" };

    // Names the three fetches in the once-then-quiet log lines.
    private const string GraphFetch = "Graph pending-approval fetch";
    private const string ArmFetch = "ARM pending-approval fetch";
    private const string GroupFetch = "PIM for Groups pending-approval fetch";

    private readonly IGraphPimClient _graph;
    private readonly IArmPimClient _arm;
    private readonly IGraphGroupPimClient _groups;
    private readonly Func<IReadOnlySet<string>>? _relevantSubscriptions;
    private readonly Func<IReadOnlySet<string>>? _relevantManagementGroupScopes;
    private readonly HashSet<string> _seenKeys = new(StringComparer.OrdinalIgnoreCase);

    // Once-then-quiet logging for the three per-poll fetches: an expired token
    // makes all of them fail identically every interval, and only the
    // transitions (fail once, recover once) carry information. One gate per
    // source, so a failing source stays quiet without muting a healthy one.
    // See FetchFailureGate.
    private readonly FetchFailureGate _graphFetchFailures = new();
    private readonly FetchFailureGate _armFetchFailures = new();
    private readonly FetchFailureGate _groupFetchFailures = new();

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
        IGraphGroupPimClient groups,
        IPluginContext context,
        PluginTenant tenant,
        TimeSpan interval,
        Func<IReadOnlySet<string>>? relevantSubscriptions = null,
        Func<IReadOnlySet<string>>? relevantManagementGroupScopes = null)
        : base(context, tenant, interval)
    {
        _graph = graph;
        _arm = arm;
        _groups = groups;
        _relevantSubscriptions = relevantSubscriptions;
        _relevantManagementGroupScopes = relevantManagementGroupScopes;
    }

    protected override string PollKind => "Pending-approval";

    // Raised at the start and end of each PollAsync so the host can spin a
    // refresh indicator and rebuild the menu. May fire on a thread-pool thread;
    // subscribers must marshal as needed.
    public event Action? PollStarted;
    public event Action? PollCompleted;

    public string TenantDisplayName => Tenant.DisplayName;

    public bool IsPolling { get; private set; }

    // Snapshot of the most recent poll. Returns the same instance until the
    // next PollAsync completes, so callers don't need to copy.
    public IReadOnlyList<UnifiedPendingApproval> CurrentApprovals => _lastSnapshot;

    internal override async Task PollAsync(CancellationToken cancellationToken)
    {
        IsPolling = true;
        PollStarted?.Invoke();
        try
        {
            await EnsureSignedInUserIdAsync(cancellationToken).ConfigureAwait(false);

            var graphTask = FetchGraphAsync(cancellationToken);
            var armTask = FetchArmAsync(cancellationToken);
            var groupTask = FetchGroupsAsync(cancellationToken);

            var graphPending = await graphTask.ConfigureAwait(false);
            var armPending = await armTask.ConfigureAwait(false);
            var groupPending = await groupTask.ConfigureAwait(false);

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
            var all = graphPending.Concat(armPending).Concat(groupPending)
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
                    Context.Logger.LogInformation(
                        "New {Source} pending approval {ApprovalId}: {Requestor} ({RequestorId}) requesting {Role} at {Scope} (tenant {TenantId}).",
                        approval.Source,
                        approval.ApprovalId,
                        approval.PrincipalDisplay,
                        approval.RequestorPrincipalId ?? "(unknown id)",
                        approval.RoleDisplay,
                        approval.ArmScope ?? approval.ScopeDisplay,
                        Tenant.TenantId);
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
                Context.Logger.LogDebug(
                    "PIM self-approval filter disabled for tenant {TenantId}: Graph /me returned no id.",
                    Tenant.TenantId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutdown */ }
        catch (Exception ex)
        {
            // Leave _signedInUserIdResolved=false so the next poll retries.
            // A persistent failure just keeps the legacy "show everything"
            // behaviour, which is the safer default.
            Context.Logger.LogDebug(
                ex,
                "PIM self-approval filter could not resolve signed-in user for tenant {TenantId}; will retry.",
                Tenant.TenantId);
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
            LogFetchRecovered(_graphFetchFailures, GraphFetch);
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
            LogFetchFailure(_graphFetchFailures, ex, GraphFetch, "continuing with ARM and PIM for Groups");
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
                LogFetchRecovered(_armFetchFailures, ArmFetch);
                return new();
            }

            var requests = await _arm.ListPendingApprovalsAsync(scopes, ct).ConfigureAwait(false);
            LogFetchRecovered(_armFetchFailures, ArmFetch);

            return requests
                .Where(r => !string.IsNullOrWhiteSpace(r.Properties?.ApprovalId))
                .Select(r => new UnifiedPendingApproval(
                    Source: PimSource.AzureRbac,
                    ApprovalId: ArmResourceId.LastSegment(r.Properties!.ApprovalId)!,
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
            LogFetchFailure(_armFetchFailures, ex, ArmFetch, "continuing with Graph and PIM for Groups");
            return new();
        }
    }

    // PIM for Groups approvals. Unlike the directory-role feed there is no
    // scope fan-out and no beta route: one call returns every group activation
    // request waiting on the signed-in user as an approver, and the client has
    // already joined each approval to the request that carries the requestor,
    // the group, and the reason they typed.
    private async Task<List<UnifiedPendingApproval>> FetchGroupsAsync(CancellationToken ct)
    {
        try
        {
            var requests = await _groups.ListPendingApprovalsAsync(ct).ConfigureAwait(false);
            LogFetchRecovered(_groupFetchFailures, GroupFetch);

            return requests
                .Where(r => !string.IsNullOrWhiteSpace(r.Id))
                .Select(r => new UnifiedPendingApproval(
                    Source: PimSource.EntraGroup,
                    // A group approval's id IS its schedule request's id, so no
                    // separate approval id has to be carried or parsed out of a
                    // resource path the way ARM's does.
                    ApprovalId: r.Id!,
                    PrincipalDisplay: r.Principal?.DisplayName ?? r.Principal?.UserPrincipalName ?? "(unknown user)",
                    // Access id in the role slot, group in the scope slot —
                    // the same split the eligible-role rows use.
                    RoleDisplay: GroupAccess.DisplayFor(r.AccessId),
                    ScopeDisplay: r.Group?.DisplayName ?? r.GroupId ?? "(unknown group)",
                    ArmScope: null,
                    RequestorPrincipalId: r.Principal?.Id ?? r.PrincipalId,
                    RequestorJustification: r.Justification))
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return new(); }
        catch (Exception ex)
        {
            LogFetchFailure(_groupFetchFailures, ex, GroupFetch, "continuing with Graph and ARM");
            return new();
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

            var choice = await Context.Notifier.ShowAsync(
                new ChoiceRequest(
                    Title: $"PIM approval — {Tenant.DisplayName}",
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
                Context.Logger.LogDebug(
                    "{Source} approval {ApprovalId} dismissed without decision on tenant {TenantId}.",
                    approval.Source, approval.ApprovalId, Tenant.TenantId);
                return;
            }

            var decision = picked switch
            {
                "Approve" => ApprovalDecision.Approve,
                "Reject" => ApprovalDecision.Deny,
                _ => (ApprovalDecision?)null,
            };
            if (decision is null) return;

            var justification = await Context.Notifier.ShowAsync(
                new TextInputRequest(
                    Title: $"Justification — {decision}",
                    Message: $"Why are you {(decision == ApprovalDecision.Approve ? "approving" : "rejecting")} {approval.RoleDisplay}?",
                    Placeholder: "Reason for this decision (required)"),
                cancellationToken).ConfigureAwait(false);

            if (justification is not TextInputResult { Text: { } justText } || string.IsNullOrWhiteSpace(justText))
            {
                Context.Logger.LogInformation(
                    "{Source} approval {ApprovalId} on tenant {TenantId}: user dismissed at justification prompt; no action taken.",
                    approval.Source, approval.ApprovalId, Tenant.TenantId);
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

                case PimSource.EntraGroup:
                    await _groups.ReviewAsync(
                        approval.ApprovalId,
                        decision.Value,
                        justText,
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (ApprovalAlreadyDecidedException ex)
        {
            // Not a failure: an approval stage can list several approvers and
            // the first decision closes it for everyone, so a user who was
            // simply beaten to it should be told that, not shown an error they
            // can do nothing about.
            Context.Logger.LogInformation(
                "{Source} approval {ApprovalId} on tenant {TenantId} was already decided by another approver; no action taken.",
                approval.Source, approval.ApprovalId, Tenant.TenantId);

            await Context.Notifier.ShowAsync(
                new InformationRequest(
                    Title: $"Already decided: {approval.RoleDisplay}",
                    Message: $"Another approver has already answered {approval.PrincipalDisplay}'s request on {approval.ScopeDisplay}.")
                {
                    Severity = NotificationSeverity.Info,
                    Details = new[] { new NotificationDetail("Detail", ex.Message) },
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Context.Logger.LogError(
                ex,
                "Failed to handle {Source} approval {ApprovalId} on tenant {TenantId}.",
                approval.Source, approval.ApprovalId, Tenant.TenantId);
        }
    }
}
