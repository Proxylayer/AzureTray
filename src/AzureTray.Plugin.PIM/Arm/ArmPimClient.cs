using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Arm;

internal sealed class ArmPimClient : IArmPimClient
{
    private const string SubscriptionsApi = "2022-12-01";
    private const string AuthorizationApi = "2020-10-01";
    private const string ApprovalApi = "2021-01-01-preview";
    private const string EndUserExpirationRuleId = "Expiration_EndUser_Assignment";
    private const string EndUserApprovalRuleId = "Approval_EndUser_Assignment";
    private const string ExpirationRuleType = "RoleManagementPolicyExpirationRule";
    private const string ApprovalRuleType = "RoleManagementPolicyApprovalRule";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPluginContext _ctx;
    private readonly ILogger _logger;
    private readonly string _tenantId;

    public ArmPimClient(IPluginContext ctx, string tenantId)
    {
        _ctx = ctx;
        _logger = ctx.Logger;
        _tenantId = tenantId;
    }

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
                ApprovalRequired: ReadApprovalRequired(rules),
                MaxActivationDuration: ReadMaxActivationDuration(rules));
        }

        _logger.LogDebug(
            "Read {PolicyCount} ARM role policies for tenant {TenantId} from {AssignmentCount} assignment(s).",
            policies.Count, _tenantId, tagged.Count);

        return policies;
    }

    // Expiration_EndUser_Assignment is the only rule that governs a user
    // self-activating an eligible role; the Admin_* expiration rules are
    // days-scale caps on eligibility/assignment and must never substitute for
    // it. Matched on id + ruleType, never on target — ARM and Graph disagree on
    // the casing of target values.
    private static TimeSpan? ReadMaxActivationDuration(List<ArmPolicyRule> rules)
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserExpirationRuleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.RuleType, ExpirationRuleType, StringComparison.OrdinalIgnoreCase));

        return Iso8601Duration.TryParse(rule?.MaximumDuration);
    }

    private static bool? ReadApprovalRequired(List<ArmPolicyRule> rules)
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserApprovalRuleId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(r.RuleType, ApprovalRuleType, StringComparison.OrdinalIgnoreCase));

        return rule?.Setting?.IsApprovalRequired;
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
                        duration = FormatIso8601Duration(duration),
                    },
                },
            },
        };

        var created = await PutScheduleRequestAsync(
            scope, requestId, body, "self-activation", cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Submitted ARM self-activation {RequestId} for role {RoleId} at {Scope} (tenant {TenantId}, status {Status}).",
            requestId, roleDefinitionId, scope, _tenantId, created.Properties?.Status);

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

        _logger.LogInformation(
            "Submitted ARM self-deactivation {RequestId} for role {RoleId} at {Scope} (tenant {TenantId}, status {Status}).",
            requestId, roleDefinitionId, scope, _tenantId, created.Properties?.Status);

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
            throw new InvalidOperationException(
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

        using var request = new HttpRequestMessage(HttpMethod.Patch, stageUrl)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "{Decision} ARM approval {ApprovalId} stage {StageId} (tenant {TenantId}).",
            decision, approvalId, openStage.Name, _tenantId);
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
                _logger.LogWarning(
                    "ARM {Operation} {RequestId} returned 409 for our own request id (a per-attempt timeout aborted the " +
                    "socket after ARM had committed it, then the retry re-sent it); reconciling by reading the committed " +
                    "request (tenant {TenantId}).",
                    operation, requestId, _tenantId);

                var existing = await GetJsonAsync<ArmRoleAssignmentScheduleRequest>(url, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    return existing;
                }

                throw new InvalidOperationException(
                    $"ARM {operation} {requestId} returned a self-id 409 but the committed request could not be read back.");
            }

            // A 409 that does NOT name our requestId is a genuine conflict — throw.
            throw BuildArmError(response, conflictBody);
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
            if (batch.Length == FanOutBatchSize)
            {
                try { await Task.Delay(FanOutBatchPause, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        return combined;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string firstUrl, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        string? next = firstUrl;
        while (!string.IsNullOrEmpty(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await GetJsonAsync<ArmCollection<T>>(next, cancellationToken);
            if (page?.Value is not null) results.AddRange(page.Value);
            next = NormalizeNextLink(page?.NextLink);
        }
        return results;
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _ctx.GetHttpClient(_tenantId).SendAsync(
            PluginHttpClientNames.Arm,
            _ctx.ArmScope,
            request,
            cancellationToken);

    // ARM returns structured JSON errors with code + message on 4xx/5xx.
    // EnsureSuccessStatusCode would discard the body; preserve it in the
    // exception so the call site can show the user what actually went wrong.
    private static async Task EnsureSuccessOrThrowWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        var body = await ReadBodySafeAsync(response, cancellationToken).ConfigureAwait(false);
        throw BuildArmError(response, body);
    }

    private static async Task<string> ReadBodySafeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return "(body unreadable)";
        }
    }

    private static HttpRequestException BuildArmError(HttpResponseMessage response, string body)
    {
        if (body.Length > 1500) body = body[..1500] + "…(truncated)";

        return new HttpRequestException(
            $"ARM {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private static string NormalizeScope(string scope)
    {
        var trimmed = scope.Trim().TrimStart('/');
        return string.IsNullOrEmpty(trimmed) ? string.Empty : trimmed + "/";
    }

    private static string? NormalizeNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink)) return null;
        if (Uri.TryCreate(nextLink, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery.TrimStart('/');
        }
        return nextLink;
    }

    private static string FormatIso8601Duration(TimeSpan duration)
    {
        var totalMinutes = (long)Math.Round(duration.TotalMinutes);
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return (hours, minutes) switch
        {
            (0, var m) => $"PT{m}M",
            (var h, 0) => $"PT{h}H",
            (var h, var m) => $"PT{h}H{m}M",
        };
    }
}
