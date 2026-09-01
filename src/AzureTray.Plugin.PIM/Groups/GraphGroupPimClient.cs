using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups.Dto;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Groups;

// PIM for Groups over Microsoft Graph v1.0. Everything here is GA — there is no
// beta route in this file, unlike the directory-role approvals which only exist
// on beta.
//
// The shape differs from directory roles in ways that are easy to get wrong:
//   * no roleDefinitionId — the "role" is accessId, "member" or "owner",
//   * no directoryScopeId — the scope is the group,
//   * approvals hang off `stages`, not `steps`, and live under the
//     privilegedAccess/group root rather than roleManagement/directory,
//   * activation policies are read one group at a time (each request returns
//     that group's member and owner policy together); there is no tenant-wide
//     bulk form the way there is for directory roles.
internal sealed class GraphGroupPimClient : GraphHttpClientBase, IGraphGroupPimClient
{
    private const string Root = "v1.0/identityGovernance/privilegedAccess/group";

    // Graph's status string for a request sitting with an approver. Spelled
    // exactly as the directory-role one, and matched case-insensitively because
    // status is an open string set.
    private const string PendingApprovalStatus = "PendingApproval";

    private const string StageInProgress = "InProgress";

    // Group display names and activation policies are read one group at a time.
    // Cap the requests in flight so a user eligible on dozens of groups issues a
    // steady trickle rather than one burst; Graph publishes no PIM-specific
    // limit, but its per-app throttling is real and a burst is what trips it.
    private const int FanOutBatchSize = 4;

    // Display names for groups seen this session, keyed by lower-cased group id.
    // A group's name is read once and reused for the life of the process: it is
    // pure presentation, it changes rarely, and re-reading it every 30-minute
    // poll would cost one request per eligible group forever. A rename shows up
    // after the next restart.
    private readonly ConcurrentDictionary<string, string> _groupNames =
        new(StringComparer.OrdinalIgnoreCase);

    // $expand on these resources is documented, but whether the PIM for Groups
    // scopes alone authorize the expansion is not — a tenant can answer 4xx for
    // the expanded form while the bare form succeeds. Each expand is therefore
    // probed rather than assumed: the first rejection turns it off for the
    // session and the caller falls back to resolving names separately.
    private readonly ExpandProbe _eligibilityGroupExpand = new();
    private readonly ExpandProbe _requestPrincipalExpand = new();

    public GraphGroupPimClient(IPluginContext ctx, string tenantId)
        : base(ctx, tenantId)
    {
    }

    // filterByCurrentUser(on='principal') is preferred over the plain list form:
    // the plain form makes $filter mandatory and would need the signed-in
    // user's object id resolved first, which this client would otherwise never
    // have to ask for.
    public async Task<IReadOnlyList<GroupEligibilityScheduleInstance>> ListEligibleGroupsAsync(
        CancellationToken cancellationToken)
    {
        var instances = await GetAllPagesWithOptionalExpandAsync<GroupEligibilityScheduleInstance>(
            $"{Root}/eligibilityScheduleInstances/filterByCurrentUser(on='principal')",
            "?$expand=group",
            _eligibilityGroupExpand,
            "eligible group memberships",
            cancellationToken).ConfigureAwait(false);

        RememberNames(instances.Select(i => i.Group));

        var resolved = await ResolveGroupNamesAsync(
            instances
                .Where(i => string.IsNullOrWhiteSpace(i.Group?.DisplayName))
                .Select(i => i.GroupId),
            cancellationToken).ConfigureAwait(false);

        // A name that could not be resolved degrades to the bare group id. It
        // reads poorly, but a row the user can still activate beats a row that
        // silently vanished because a display name was unavailable.
        for (var i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (!string.IsNullOrWhiteSpace(instance.Group?.DisplayName)) continue;
            if (string.IsNullOrWhiteSpace(instance.GroupId)) continue;

            var name = resolved.TryGetValue(instance.GroupId!, out var found)
                ? found
                : instance.GroupId!;
            instances[i] = instance with { Group = new GroupRef(instance.GroupId, name) };
        }

        return instances;
    }

    public async Task<IReadOnlyList<GroupAssignmentScheduleInstance>> ListActiveGroupAssignmentsAsync(
        CancellationToken cancellationToken)
    {
        // No $expand: an active assignment is only ever matched against an
        // eligible row (on group id + access id), never rendered on its own, so
        // the group's display name would be dead weight here.
        return await GetAllPagesAsync<GroupAssignmentScheduleInstance>(
            $"{Root}/assignmentScheduleInstances/filterByCurrentUser(on='principal')",
            cancellationToken).ConfigureAwait(false);
    }

    // Policy assignments are NOT under the privilegedAccess root — they are the
    // same tenant-level roleManagementPolicyAssignments collection the directory
    // roles use, distinguished by scopeType 'Group' and a scopeId that is the
    // group's object id. Omitting roleDefinitionId from the filter is deliberate:
    // it returns both of the group's policies (the assignment's roleDefinitionId
    // is then the literal 'member' or 'owner') in one request instead of two.
    //
    // policyId is intentionally never cached — PIM re-creates the policy when a
    // group is onboarded, so a remembered id goes stale silently. The scope
    // filter is the stable identity.
    public async Task<IReadOnlyDictionary<GroupRolePolicyKey, RolePolicy>> GetGroupPoliciesAsync(
        IEnumerable<string> groupIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(groupIds);

        var distinct = Distinct(groupIds);
        var policies = new Dictionary<GroupRolePolicyKey, RolePolicy>();
        if (distinct.Count == 0) return policies;

        var perGroup = await InBatchesAsync(
            distinct,
            async groupId =>
            {
                var url =
                    "v1.0/policies/roleManagementPolicyAssignments" +
                    $"?$filter=scopeId eq '{groupId}' and scopeType eq 'Group'" +
                    "&$expand=policy($expand=rules)";
                var assignments = await GetAllPagesAsync<EntraPolicyAssignment>(url, cancellationToken)
                    .ConfigureAwait(false);
                return (GroupId: groupId, Assignments: assignments);
            },
            cancellationToken).ConfigureAwait(false);

        foreach (var (groupId, assignments) in perGroup)
        {
            foreach (var assignment in assignments)
            {
                // roleDefinitionId on a Group-scoped assignment is the access
                // id, not a GUID — that is the join back to an eligible row.
                var accessId = assignment.RoleDefinitionId;
                if (string.IsNullOrWhiteSpace(accessId)) continue;

                var rules = assignment.Policy?.RulesToRead;
                if (rules is null) continue;

                policies[GroupRolePolicyKey.For(groupId, accessId)] = new RolePolicy(
                    ApprovalRequired: ReadApprovalRequired(rules),
                    MaxActivationDuration: ReadMaxActivationDuration(rules));
            }
        }

        Logger.LogDebug(
            "Read {PolicyCount} PIM for Groups policies for tenant {TenantId} across {GroupCount} group(s).",
            policies.Count, TenantId, distinct.Count);

        return policies;
    }

    public async Task<GroupScheduleRequest> ActivateAsync(
        string principalId,
        string groupId,
        string accessId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Activation duration must be positive.");
        }

        var normalizedAccess = GroupAccess.Normalize(accessId);

        var body = new
        {
            accessId = normalizedAccess,
            principalId,
            groupId,
            action = "selfActivate",
            justification,
            scheduleInfo = new
            {
                // Null (omitted by WhenWritingNull) means "start now". Same
                // reasoning as GraphPimClient.ActivateRoleAsync: sending
                // DateTimeOffset.UtcNow is racy, because the moment is already
                // in the past by the time Graph evaluates the request and Graph
                // rejects a past startDateTime.
                startDateTime = (string?)null,
                expiration = new
                {
                    type = "afterDuration",
                    duration = FormatIso8601Duration(duration),
                },
            },
        };

        var created = await PostJsonAsync<GroupScheduleRequest>(
            $"{Root}/assignmentScheduleRequests", body, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Graph returned an empty body for group self-activation.");

        Logger.LogInformation(
            "Submitted group self-activation {RequestId} for {AccessId} of group {GroupId} on tenant {TenantId} ({Status}).",
            created.Id, normalizedAccess, groupId, TenantId, created.Status);

        return created;
    }

    public async Task<GroupScheduleRequest> DeactivateAsync(
        string principalId,
        string groupId,
        string accessId,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var normalizedAccess = GroupAccess.Normalize(accessId);
        var reason = string.IsNullOrWhiteSpace(justification) ? null : justification;

        GroupScheduleRequest? created;
        try
        {
            // Whether scheduleInfo may be omitted for selfDeactivate is not
            // documented either way. Omitting it is the shape that matches the
            // directory-role deactivation (which is immediate and carries no
            // schedule), so it is what we send first.
            created = await PostJsonAsync<GroupScheduleRequest>(
                $"{Root}/assignmentScheduleRequests",
                new
                {
                    accessId = normalizedAccess,
                    principalId,
                    groupId,
                    action = "selfDeactivate",
                    justification = reason,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (HasStatus(ex, HttpStatusCode.BadRequest))
        {
            // Empirically resolved fallback for the undocumented half of the
            // contract: if the service insists on a scheduleInfo, give it the
            // most degenerate one that can mean "now, for no time at all".
            Logger.LogDebug(
                ex,
                "Group self-deactivation without scheduleInfo was rejected for group {GroupId} on tenant {TenantId}; retrying with a zero-duration scheduleInfo.",
                groupId, TenantId);

            created = await PostJsonAsync<GroupScheduleRequest>(
                $"{Root}/assignmentScheduleRequests",
                new
                {
                    accessId = normalizedAccess,
                    principalId,
                    groupId,
                    action = "selfDeactivate",
                    justification = reason,
                    scheduleInfo = new
                    {
                        startDateTime = (string?)null,
                        expiration = new { type = "afterDuration", duration = "PT0S" },
                    },
                },
                cancellationToken).ConfigureAwait(false);
        }

        if (created is null)
        {
            throw new InvalidOperationException("Graph returned an empty body for group self-deactivation.");
        }

        // A 2xx here is not proof the access is gone. When removing the last
        // active OWNER would leave the group ownerless, PIM accepts the request
        // and then silently retries the removal for up to 30 days, so the
        // membership can outlive an apparently successful deactivation.
        Logger.LogInformation(
            "Submitted group self-deactivation {RequestId} for {AccessId} of group {GroupId} on tenant {TenantId} ({Status}).",
            created.Id, normalizedAccess, groupId, TenantId, created.Status);

        return created;
    }

    // Two steps, because the approval object carries nothing but its id and its
    // stages: list what the signed-in user may decide, then read each one's
    // underlying request — which shares the approval's id — for the requestor,
    // the group, and the justification the approver actually needs to see.
    public async Task<IReadOnlyList<GroupScheduleRequest>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken)
    {
        var approvals = await GetAllPagesAsync<GroupApproval>(
            $"{Root}/assignmentApprovals/filterByCurrentUser(on='approver')",
            cancellationToken).ConfigureAwait(false);

        var ids = approvals
            .Select(a => a.Id)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (ids.Count == 0) return Array.Empty<GroupScheduleRequest>();

        var requests = await InBatchesAsync(
            ids,
            async id => await ReadPendingRequestAsync(id, cancellationToken).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        var pending = requests.Where(r => r is not null).Select(r => r!).ToList();

        await FillGroupNamesAsync(pending, cancellationToken).ConfigureAwait(false);

        return pending;
    }

    // Null when this approval should not be surfaced — already decided, or its
    // request could not be read. One unreadable request must not take the whole
    // approver feed down with it, so the failure is swallowed at Debug.
    private async Task<GroupScheduleRequest?> ReadPendingRequestAsync(
        string approvalId, CancellationToken cancellationToken)
    {
        try
        {
            var request = await GetJsonWithOptionalExpandAsync<GroupScheduleRequest>(
                $"{Root}/assignmentScheduleRequests/{approvalId}",
                "?$expand=principal,group",
                _requestPrincipalExpand,
                "group activation requests",
                cancellationToken).ConfigureAwait(false);

            if (request is null) return null;

            // The approver list can include approvals whose request has since
            // been decided or withdrawn; only a still-pending one is actionable.
            return string.Equals(request.Status, PendingApprovalStatus, StringComparison.OrdinalIgnoreCase)
                ? request with { Id = request.Id ?? approvalId }
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogDebug(
                ex,
                "Could not read the group activation request behind approval {ApprovalId} on tenant {TenantId}; skipping it this poll.",
                approvalId, TenantId);
            return null;
        }
    }

    public async Task ReviewAsync(
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        // Stages come back inline on a GET — unlike the directory-role approval,
        // which needs $expand=steps.
        var approval = await GetJsonAsync<GroupApproval>(
            $"{Root}/assignmentApprovals/{approvalId}", cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Group approval {approvalId} not found.");

        var openStage = approval.Stages?
            .FirstOrDefault(s => string.Equals(s.Status, StageInProgress, StringComparison.OrdinalIgnoreCase));
        if (openStage?.Id is null)
        {
            // No open stage almost always means somebody else decided between
            // the poll that surfaced this and the user clicking.
            throw new ApprovalAlreadyDecidedException(approvalId);
        }

        var reviewResult = decision == ApprovalDecision.Approve ? "Approve" : "Deny";

        try
        {
            await PatchJsonAsync(
                $"{Root}/assignmentApprovals/{approvalId}/stages/{openStage.Id}",
                new { reviewResult, justification },
                cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (HasStatus(ex, HttpStatusCode.Conflict))
        {
            // A stage can list several approvers and the first decision closes
            // it for everyone; 409 is that race, not a fault.
            throw new ApprovalAlreadyDecidedException(approvalId, ex);
        }

        Logger.LogInformation(
            "{Decision} group approval {ApprovalId} stage {StageId} on tenant {TenantId}.",
            decision, approvalId, openStage.Id, TenantId);
    }

    public async Task<string?> GetActivationStatusAsync(
        string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var status = await GetJsonAsync<GroupScheduleRequestStatus>(
            $"{Root}/assignmentScheduleRequests/{requestId}", cancellationToken).ConfigureAwait(false);
        return status?.Status;
    }

    // Fills in the display name on requests whose group was not expanded, so an
    // approval prompt names the group rather than a GUID.
    private async Task FillGroupNamesAsync(
        List<GroupScheduleRequest> requests, CancellationToken cancellationToken)
    {
        RememberNames(requests.Select(r => r.Group));

        var resolved = await ResolveGroupNamesAsync(
            requests
                .Where(r => string.IsNullOrWhiteSpace(r.Group?.DisplayName))
                .Select(r => r.GroupId),
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < requests.Count; i++)
        {
            var request = requests[i];
            if (!string.IsNullOrWhiteSpace(request.Group?.DisplayName)) continue;
            if (string.IsNullOrWhiteSpace(request.GroupId)) continue;

            var name = resolved.TryGetValue(request.GroupId!, out var found) ? found : request.GroupId!;
            requests[i] = request with { Group = new GroupRef(request.GroupId, name) };
        }
    }

    // Display names for the given groups, from the session cache where possible
    // and one GET per group otherwise. A group that cannot be read (deleted,
    // or not visible to the signed-in user) is simply absent from the result —
    // callers substitute the group id rather than losing the row.
    private async Task<Dictionary<string, string>> ResolveGroupNamesAsync(
        IEnumerable<string?> groupIds, CancellationToken cancellationToken)
    {
        var resolved = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();

        foreach (var groupId in Distinct(groupIds))
        {
            if (_groupNames.TryGetValue(groupId, out var cached)) resolved[groupId] = cached;
            else missing.Add(groupId);
        }
        if (missing.Count == 0) return resolved;

        async Task<(string GroupId, string? DisplayName)> ReadNameAsync(string groupId)
        {
            try
            {
                var group = await GetJsonAsync<GroupRef>(
                    $"v1.0/groups/{groupId}?$select=id,displayName", cancellationToken)
                    .ConfigureAwait(false);
                return (groupId, group?.DisplayName);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger.LogDebug(
                    ex,
                    "Could not read the display name of group {GroupId} on tenant {TenantId}; the menu will show its id.",
                    groupId, TenantId);
                return (groupId, null);
            }
        }

        var fetched = await InBatchesAsync(missing, ReadNameAsync, cancellationToken).ConfigureAwait(false);

        foreach (var (groupId, displayName) in fetched)
        {
            if (string.IsNullOrWhiteSpace(displayName)) continue;
            _groupNames[groupId] = displayName!;
            resolved[groupId] = displayName!;
        }

        return resolved;
    }

    private void RememberNames(IEnumerable<GroupRef?> groups)
    {
        foreach (var group in groups)
        {
            if (group?.Id is not { } id || string.IsNullOrWhiteSpace(id)) continue;
            if (string.IsNullOrWhiteSpace(group.DisplayName)) continue;
            _groupNames[id] = group.DisplayName!;
        }
    }

    private static List<string> Distinct(IEnumerable<string?> values)
        => values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    // Runs body over the inputs a batch at a time, preserving nothing about
    // order — every caller here keys its results rather than positioning them.
    private static async Task<List<TResult>> InBatchesAsync<TInput, TResult>(
        List<TInput> inputs,
        Func<TInput, Task<TResult>> body,
        CancellationToken cancellationToken)
    {
        var results = new List<TResult>(inputs.Count);
        foreach (var batch in inputs.Chunk(FanOutBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.AddRange(await Task.WhenAll(batch.Select(body)).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<List<T>> GetAllPagesWithOptionalExpandAsync<T>(
        string url,
        string expandQuery,
        ExpandProbe probe,
        string what,
        CancellationToken cancellationToken)
        => await WithOptionalExpandAsync(
            probe, what,
            expanded => GetAllPagesAsync<T>(expanded ? url + expandQuery : url, cancellationToken))
            .ConfigureAwait(false);

    private async Task<T?> GetJsonWithOptionalExpandAsync<T>(
        string url,
        string expandQuery,
        ExpandProbe probe,
        string what,
        CancellationToken cancellationToken)
        => await WithOptionalExpandAsync(
            probe, what,
            expanded => GetJsonAsync<T>(expanded ? url + expandQuery : url, cancellationToken))
            .ConfigureAwait(false);

    // Runs the read with $expand when the probe still believes it works, and
    // once more without it when Graph rejects the expanded form. A rejection
    // disables the expand for the session — but only if the bare form then
    // succeeds: if both fail, the expand was never proven to be the problem
    // (a missing scope fails either way), so the probe is put back.
    private async Task<TResult> WithOptionalExpandAsync<TResult>(
        ExpandProbe probe,
        string what,
        Func<bool, Task<TResult>> read)
    {
        if (!probe.Usable) return await read(false).ConfigureAwait(false);

        try
        {
            return await read(true).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (IsClientError(ex))
        {
            probe.Usable = false;
            Logger.LogDebug(
                ex,
                "Graph rejected the expanded form when reading {What} for tenant {TenantId}; falling back to unexpanded reads for the rest of this session.",
                what, TenantId);
        }

        try
        {
            return await read(false).ConfigureAwait(false);
        }
        catch
        {
            probe.Usable = true;
            throw;
        }
    }

    private sealed class ExpandProbe
    {
        // Read and written from concurrent polls; a torn read costs at most one
        // redundant attempt, so volatile is sufficient and a lock is not.
        private volatile bool _usable = true;

        public bool Usable
        {
            get => _usable;
            set => _usable = value;
        }
    }
}
