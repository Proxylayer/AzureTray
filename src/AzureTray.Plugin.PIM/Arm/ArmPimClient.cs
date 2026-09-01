using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Http;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Arm;

internal sealed class ArmPimClient : PimHttpClientBase, IArmPimClient
{
    private const string SubscriptionsApi = "2022-12-01";
    private const string AuthorizationApi = "2020-10-01";
    private const string ApprovalApi = "2021-01-01-preview";

    public ArmPimClient(IPluginContext ctx, string tenantId)
        : base(ctx, tenantId, PluginHttpClientNames.Arm, () => ctx.ArmScope, "ARM")
    {
    }

    protected override async Task<IPageEnvelope<T>?> ReadPageAsync<T>(string url, CancellationToken cancellationToken)
        => await GetJsonAsync<ArmCollection<T>>(url, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<ArmSubscription>> ListSubscriptionsAsync(
        CancellationToken cancellationToken)
    {
        var url = $"subscriptions?api-version={SubscriptionsApi}";
        return await GetAllPagesAsync<ArmSubscription>(url, cancellationToken);
    }

    public Task<IReadOnlyList<ArmRoleAssignmentScheduleRequest>> ListPendingApprovalsAsync(
        IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        return FanOutScopesAsync<ArmRoleAssignmentScheduleRequest>(
            scopes,
            prefix =>
                $"{prefix}providers/Microsoft.Authorization/roleAssignmentScheduleRequests" +
                $"?api-version={AuthorizationApi}" +
                "&$filter=asApprover()" +
                "&$expand=expandedProperties",
            cancellationToken);
    }

    public Task<IReadOnlyList<ArmEligibilitySchedule>> ListEligibleRolesAsync(
        string principalId, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(scopes);
        return FanOutScopesAsync<ArmEligibilitySchedule>(
            scopes,
            prefix =>
                $"{prefix}providers/Microsoft.Authorization/roleEligibilitySchedules" +
                $"?api-version={AuthorizationApi}" +
                $"&$filter=assignedTo('{principalId}')",
            cancellationToken);
    }

    public Task<IReadOnlyList<ArmRoleAssignmentScheduleInstance>> ListActiveRoleAssignmentsAsync(
        string principalId, IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentNullException.ThrowIfNull(scopes);
        return FanOutScopesAsync<ArmRoleAssignmentScheduleInstance>(
            scopes,
            prefix =>
                $"{prefix}providers/Microsoft.Authorization/roleAssignmentScheduleInstances" +
                $"?api-version={AuthorizationApi}" +
                // No $expand: the schedule endpoints return expandedProperties
                // by default (same as roleEligibilitySchedules above), and the
                // match is on roleDefinitionId + scope regardless.
                $"&$filter=assignedTo('{principalId}')",
            cancellationToken);
    }

    // One request per scope returns every policy assignment at that scope with
    // properties.effectiveRules inline, so the approval rule and the activation
    // duration cap both come out of this single response — no follow-up
    // GET {policyId} per role. roleManagementPolicyAssignments (not
    // roleManagementPolicies) because only the assignment carries the
    // roleDefinitionId needed to join back to an eligible role.
    public async Task<IReadOnlyDictionary<ArmRolePolicyKey, RolePolicy>> GetRolePoliciesAsync(
        IEnumerable<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);

        var tagged = await FanOutScopesTaggedAsync<ArmPolicyAssignment>(
            scopes,
            prefix =>
                $"{prefix}providers/Microsoft.Authorization/roleManagementPolicyAssignments" +
                $"?api-version={AuthorizationApi}",
            cancellationToken).ConfigureAwait(false);

        var policies = new Dictionary<ArmRolePolicyKey, RolePolicy>();
        foreach (var (scope, assignment) in tagged)
        {
            var roleDefinitionId = assignment.Properties?.RoleDefinitionId;
            if (string.IsNullOrWhiteSpace(roleDefinitionId)) continue;

            var rules = assignment.Properties?.EffectiveRules;
            if (rules is null) continue;

            policies[ArmRolePolicyKey.For(scope, roleDefinitionId)] = new RolePolicy(
                ApprovalRequired: PolicyRules.ReadApprovalRequired(rules),
                MaxActivationDuration: PolicyRules.ReadMaxActivationDuration(rules));
        }

        Logger.LogDebug(
            "Read {PolicyCount} ARM role policies for tenant {TenantId} from {AssignmentCount} assignment(s).",
            policies.Count, TenantId, tagged.Count);

        return policies;
    }

    public async Task<ArmRoleAssignmentScheduleRequest> ActivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string? linkedRoleEligibilityScheduleId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Activation duration must be positive.");
        }

        var requestId = Guid.NewGuid().ToString();

        var body = new
        {
            properties = new
            {
                principalId,
                roleDefinitionId,
                requestType = "SelfActivate",
                justification,
                linkedRoleEligibilityScheduleId = string.IsNullOrWhiteSpace(linkedRoleEligibilityScheduleId)
                    ? null
                    : linkedRoleEligibilityScheduleId,
                scheduleInfo = new
                {
                    // See GraphPimClient.ActivateRoleAsync — sending a UtcNow
                    // timestamp here is racy because by the time ARM
                    // evaluates the request, the moment is already in the
                    // past and ARM rejects past start times. Null (omitted
                    // via WhenWritingNull) means "start now".
                    startDateTime = (string?)null,
                    expiration = new
                    {
                        type = "AfterDuration",
                        duration = Iso8601Duration.Format(duration),
                    },
                },
            },
        };

        var created = await PutScheduleRequestAsync(
            scope, requestId, body, "self-activation", cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "Submitted ARM self-activation {RequestId} for role {RoleId} at {Scope} (tenant {TenantId}, status {Status}).",
            requestId, roleDefinitionId, scope, TenantId, created.Properties?.Status);

        return created;
    }

    public async Task<ArmRoleAssignmentScheduleRequest> DeactivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefinitionId);

        var requestId = Guid.NewGuid().ToString();

        // SelfDeactivate is immediate — no scheduleInfo and no linked
        // eligibility id (those only matter when granting access). Justification
        // is optional for deactivation; omit when blank.
        var body = new
        {
            properties = new
            {
                principalId,
                roleDefinitionId,
                requestType = "SelfDeactivate",
                justification = string.IsNullOrWhiteSpace(justification) ? null : justification,
            },
        };

        var created = await PutScheduleRequestAsync(
            scope, requestId, body, "self-deactivation", cancellationToken).ConfigureAwait(false);

        Logger.LogInformation(
            "Submitted ARM self-deactivation {RequestId} for role {RoleId} at {Scope} (tenant {TenantId}, status {Status}).",
            requestId, roleDefinitionId, scope, TenantId, created.Properties?.Status);

        return created;
    }

    public async Task ReviewAsync(
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        // Role Assignment Approvals is a tenant-level ARM collection — the URL
        // carries NO scope segment, regardless of where the underlying request
        // was made (subscription, management group, resource group). Prefixing
        // a scope happens to route at subscription level but 404s at
        // management-group level.
        var approvalUrl =
            $"providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}" +
            $"?api-version={ApprovalApi}";
        var approval = await GetJsonAsync<ArmApproval>(approvalUrl, cancellationToken)
            ?? throw new InvalidOperationException(
                $"ARM approval {approvalId} not found (tenant-level roleAssignmentApprovals).");

        var openStage = approval.Properties?.Stages?
            .FirstOrDefault(s => string.Equals(s.Properties?.Status, "InProgress", StringComparison.OrdinalIgnoreCase));
        if (openStage?.Id is null || openStage.Name is null)
        {
            // No open stage almost always means somebody else decided between
            // the poll that surfaced this and the user clicking.
            throw new ApprovalAlreadyDecidedException(
                approvalId,
                $"ARM approval {approvalId} has no open stage (already completed, or not assigned to you).");
        }

        var reviewResult = decision == ApprovalDecision.Approve ? "Approve" : "Deny";
        var stageUrl =
            $"providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}/stages/{openStage.Name}" +
            $"?api-version={ApprovalApi}";

        var body = new
        {
            properties = new
            {
                reviewResult,
                justification,
            },
        };

        try
        {
            await PatchJsonAsync(stageUrl, body, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (HasStatus(ex, HttpStatusCode.Conflict))
        {
            // A stage can list several approvers and the first decision closes
            // it for everyone; 409 is that race, not a fault.
            throw new ApprovalAlreadyDecidedException(approvalId, detail: null, ex);
        }

        Logger.LogInformation(
            "{Decision} ARM approval {ApprovalId} stage {StageId} (tenant {TenantId}).",
            decision, approvalId, openStage.Name, TenantId);
    }

    public async Task<string?> GetActivationStatusAsync(
        string scope, string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var prefix = NormalizeScope(scope);
        var url =
            $"{prefix}providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{requestId}" +
            $"?api-version={AuthorizationApi}";

        var status = await GetJsonAsync<ArmScheduleRequestStatus>(url, cancellationToken);
        return status?.Properties?.Status;
    }

    // ---- helpers ----------------------------------------------------------

    // PUT to roleAssignmentScheduleRequests/{requestId}, where requestId is a
    // client-generated GUID chosen by the caller. Owning that id is what makes
    // the write idempotent, and this method is where that pays off:
    //
    // ARM PIM write PUTs regularly take longer than the resilience handler's
    // per-attempt timeout. When one does, the socket is aborted *after* ARM has
    // already accepted and committed the request, and the handler retries the
    // identical PUT (same GUID, same URL). ARM then answers 409 Conflict —
    // "a role assignment request with Id {guid} already exists" — which is not
    // transient, so it is surfaced as a failure even though the role was in fact
    // granted. That is the spurious "Activation failed" the user sees.
    //
    // Because we generated that GUID, a 409 whose body names *this* requestId is
    // proof that our own earlier attempt won the race: the request exists and is
    // ours. We reconcile by GETting the committed request and returning it as if
    // the PUT had returned it. Any other 409 (a different id, a genuine
    // conflict) is left to throw unchanged.
    private async Task<ArmRoleAssignmentScheduleRequest> PutScheduleRequestAsync(
        string scope, string requestId, object body, string operation, CancellationToken cancellationToken)
    {
        var prefix = NormalizeScope(scope);
        var url =
            $"{prefix}providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{requestId}" +
            $"?api-version={AuthorizationApi}";

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflictBody = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
            if (conflictBody.Contains(requestId, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogWarning(
                    "ARM {Operation} {RequestId} returned 409 for our own request id (a per-attempt timeout aborted the " +
                    "socket after ARM had committed it, then the retry re-sent it); reconciling by reading the committed " +
                    "request (tenant {TenantId}).",
                    operation, requestId, TenantId);

                var existing = await GetJsonAsync<ArmRoleAssignmentScheduleRequest>(url, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }

                throw new InvalidOperationException(
                    $"ARM {operation} {requestId} returned a self-id 409 but the committed request could not be read back.");
            }

            // A 409 that does NOT name our requestId is a genuine conflict — throw.
            throw BuildError(response, conflictBody);
        }

        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<ArmRoleAssignmentScheduleRequest>(JsonOptions, cancellationToken);
        if (created is null)
        {
            throw new InvalidOperationException($"ARM returned an empty body for {operation}.");
        }
        return created;
    }


    // Per-scope fan-out tuned to avoid ARM 429s when a tenant has many
    // subscriptions: at most BatchSize parallel requests in flight, with a
    // BatchPause between batches. Matches the predecessor app's strategy.
    private const int FanOutBatchSize = 2;
    private static readonly TimeSpan FanOutBatchPause = TimeSpan.FromMilliseconds(500);

    private async Task<IReadOnlyList<T>> FanOutScopesAsync<T>(
        IEnumerable<string> scopes,
        Func<string, string> urlForScope,
        CancellationToken cancellationToken)
    {
        var tagged = await FanOutScopesTaggedAsync<T>(scopes, urlForScope, cancellationToken).ConfigureAwait(false);
        var flattened = new List<T>(tagged.Count);
        foreach (var (_, item) in tagged) flattened.Add(item);
        return flattened;
    }

    // Same fan-out, but each result keeps the scope it was read from. Policy
    // assignments need it: a policy is identified by role + scope, and the
    // requested scope is what the caller matches its roles against.
    private async Task<IReadOnlyList<(string Scope, T Item)>> FanOutScopesTaggedAsync<T>(
        IEnumerable<string> scopes,
        Func<string, string> urlForScope,
        CancellationToken cancellationToken)
    {
        var distinct = scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
        if (distinct.Count == 0) return Array.Empty<(string, T)>();

        var combined = new List<(string Scope, T Item)>();
        var processed = 0;
        foreach (var batch in distinct.Chunk(FanOutBatchSize))
        {
            var tasks = batch.Select(async scope =>
            {
                var url = urlForScope(NormalizeScope(scope));
                var items = await GetAllPagesAsync<T>(url, cancellationToken).ConfigureAwait(false);
                return (Scope: scope, Items: items);
            });
            foreach (var (scope, items) in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                foreach (var item in items) combined.Add((scope, item));
            }
            processed += batch.Length;

            // The pause spaces out consecutive batches so ARM is not hit with
            // the whole fan-out at once. Pausing after the final batch throttles
            // nothing - there is no following request - it only delays the
            // caller, so only pause while scopes remain.
            if (processed < distinct.Count)
            {
                try { await Task.Delay(FanOutBatchPause, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        return combined;
    }

    private static string NormalizeScope(string scope)
    {
        var trimmed = scope.Trim().TrimStart('/');
        return string.IsNullOrEmpty(trimmed) ? string.Empty : trimmed + "/";
    }
}
