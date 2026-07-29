using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Policies;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Graph;

internal sealed class GraphPimClient : IGraphPimClient
{
    private const string DirectoryScope = "/";
    private const string EndUserExpirationRuleId = "Expiration_EndUser_Assignment";
    private const string EndUserApprovalRuleId = "Approval_EndUser_Assignment";

    // scopeType for the tenant-wide role policies, tried in this order. A wrong
    // scopeType is not an error — Graph returns an empty set — so the value
    // cannot be verified by inspection: 'Directory' is what we have always sent,
    // 'DirectoryRole' is what Microsoft's own v1.0 example for Entra roles uses.
    // Whichever one returns assignments is remembered for the session.
    private static readonly string[] ScopeTypeCandidates = ["Directory", "DirectoryRole"];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPluginContext _ctx;
    private readonly ILogger _logger;
    private readonly string _tenantId;

    // The scopeType that was proven to return policy assignments for this
    // tenant, so the probe below costs one extra request once rather than one
    // per poll. Written by whichever poll confirms it and read by all of them —
    // two watchers can call GetRolePoliciesAsync concurrently, and the worst a
    // stale read can cost is a repeated probe.
    private volatile string? _confirmedScopeType;

    public GraphPimClient(IPluginContext ctx, string tenantId)
    {
        _ctx = ctx;
        _logger = ctx.Logger;
        _tenantId = tenantId;
    }

    public async Task<string?> GetSignedInUserIdAsync(CancellationToken cancellationToken)
    {
        var me = await GetJsonAsync<EntraMe>("v1.0/me?$select=id", cancellationToken);
        return me?.Id;
    }

    public async Task<IReadOnlyList<EntraEligibilitySchedule>> ListActiveRoleAssignmentsAsync(
        string principalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url =
            "v1.0/roleManagement/directory/roleAssignmentScheduleInstances" +
            $"?$filter=principalId eq '{principalId}'" +
            "&$expand=roleDefinition";

        return await GetAllPagesAsync<EntraEligibilitySchedule>(url, cancellationToken);
    }

    public async Task<IReadOnlyList<EntraEligibilitySchedule>> ListEligibleRolesAsync(
        string principalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url =
            "v1.0/roleManagement/directory/roleEligibilitySchedules" +
            $"?$filter=principalId eq '{principalId}'" +
            "&$expand=roleDefinition,principal";

        return await GetAllPagesAsync<EntraEligibilitySchedule>(url, cancellationToken);
    }

    public async Task<IReadOnlyList<EntraScheduleRequest>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken)
    {
        var url =
            "v1.0/roleManagement/directory/roleAssignmentScheduleRequests" +
            "?$filter=status eq 'PendingApproval'" +
            "&$expand=principal,roleDefinition";

        return await GetAllPagesAsync<EntraScheduleRequest>(url, cancellationToken);
    }

    // One request covers the whole tenant: every directory-scoped policy
    // assignment, each with its policy's effective rules expanded, keyed by the
    // role definition id the assignment names (a bare GUID for Graph, so it
    // joins straight to an eligible role's RoleDefinitionId).
    //
    // effectiveRules is expanded WITHOUT a nested $select. It is a collection of
    // the base type unifiedRoleManagementPolicyRule, and maximumDuration /
    // isExpirationRequired / setting live only on the derived rule types, so
    // naming them in a nested $select is rejected while OData parses the query:
    // "Could not find a property named 'maximumDuration' on type
    // 'microsoft.graph.unifiedRoleManagementPolicyRule'". That 400 shipped, and
    // every poll fell back to "cap unknown" without a cap ever being read. The
    // fuller payload (17 rules per policy) is the price of the query working.
    //
    // Requires the signed-in user to hold a directory role that permits
    // reading policies (Global Reader, Security Reader/Operator/Administrator,
    // Privileged Role Administrator); users with none get a 403. That throws
    // out of here and the caller degrades to "cap unknown".
    public async Task<IReadOnlyDictionary<string, RolePolicy>> GetRolePoliciesAsync(
        CancellationToken cancellationToken)
    {
        var (assignments, scopeTypesTried) = await ReadPolicyAssignmentsAsync(cancellationToken);

        var policies = new Dictionary<string, RolePolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in assignments)
        {
            var roleDefinitionId = assignment.RoleDefinitionId;
            if (string.IsNullOrWhiteSpace(roleDefinitionId)) continue;

            var rules = assignment.Policy?.EffectiveRules;
            if (rules is null) continue;

            policies[roleDefinitionId] = new RolePolicy(
                ApprovalRequired: ReadApprovalRequired(rules),
                MaxActivationDuration: ReadMaxActivationDuration(rules));
        }

        // A successful-but-empty read is the failure mode that hid the broken
        // $select for a whole release: it looks exactly like a healthy poll from
        // the outside. Say so out loud instead.
        if (assignments.Count == 0)
        {
            _logger.LogWarning(
                "Entra role-policy read for tenant {TenantId} succeeded but returned no policy assignments (scopeType {ScopeTypesTried}). Activation caps and approval requirements stay unknown for Entra roles.",
                _tenantId, scopeTypesTried);
        }
        else
        {
            _logger.LogDebug(
                "Read {PolicyCount} Entra role policies for tenant {TenantId} from {AssignmentCount} assignment(s).",
                policies.Count, _tenantId, assignments.Count);
        }

        return policies;
    }

    // Self-diagnosing scopeType probe: the candidate that returns assignments
    // wins and is cached for the session. Returns the assignments plus the
    // scopeType(s) actually queried, so a zero-result read can name them.
    private async Task<(List<EntraPolicyAssignment> Assignments, string ScopeTypesTried)>
        ReadPolicyAssignmentsAsync(CancellationToken cancellationToken)
    {
        var confirmed = _confirmedScopeType;
        if (confirmed is not null)
        {
            var cached = await GetAllPagesAsync<EntraPolicyAssignment>(
                PolicyAssignmentsUrl(confirmed), cancellationToken);
            return (cached, confirmed);
        }

        List<EntraPolicyAssignment> assignments = [];
        foreach (var scopeType in ScopeTypeCandidates)
        {
            assignments = await GetAllPagesAsync<EntraPolicyAssignment>(
                PolicyAssignmentsUrl(scopeType), cancellationToken);
            if (assignments.Count == 0) continue;

            _confirmedScopeType = scopeType;
            _logger.LogInformation(
                "Entra role policies for tenant {TenantId} read with scopeType '{ScopeType}' ({AssignmentCount} assignment(s)); using it for the rest of this session.",
                _tenantId, scopeType, assignments.Count);
            return (assignments, scopeType);
        }

        // Nothing confirmed — every candidate is retried on the next poll.
        return (assignments, string.Join(" then ", ScopeTypeCandidates));
    }

    private static string PolicyAssignmentsUrl(string scopeType) =>
        "v1.0/policies/roleManagementPolicyAssignments" +
        $"?$filter=scopeId eq '{DirectoryScope}' and scopeType eq '{scopeType}'" +
        "&$expand=policy($expand=effectiveRules)";

    // Expiration_EndUser_Assignment is the only rule that governs a user
    // self-activating an eligible role. The other expiration rules
    // (Expiration_Admin_Eligibility, Expiration_Admin_Assignment) are
    // days-scale admin caps and must never stand in for it.
    private static TimeSpan? ReadMaxActivationDuration(List<EntraPolicyRule> rules)
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserExpirationRuleId, StringComparison.OrdinalIgnoreCase)
            && IsRuleType(r.ODataType, "unifiedRoleManagementPolicyExpirationRule"));

        return Iso8601Duration.TryParse(rule?.MaximumDuration);
    }

    private static bool? ReadApprovalRequired(List<EntraPolicyRule> rules)
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserApprovalRuleId, StringComparison.OrdinalIgnoreCase)
            && IsRuleType(r.ODataType, "unifiedRoleManagementPolicyApprovalRule"));

        return rule?.Setting?.IsApprovalRequired;
    }

    // @odata.type is a corroborating check only: the rule ids are unique within
    // a policy, so an absent type is accepted rather than treated as a mismatch
    // (an unexpanded or narrowed payload can arrive without it).
    private static bool IsRuleType(string? odataType, string expected)
        => string.IsNullOrWhiteSpace(odataType)
            || odataType.Contains(expected, StringComparison.OrdinalIgnoreCase);

    public async Task<EntraScheduleRequest> ActivateRoleAsync(
        string principalId,
        string roleDefinitionId,
        string? directoryScopeId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefinitionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), "Activation duration must be positive.");
        }

        var body = new
        {
            action = "selfActivate",
            principalId,
            roleDefinitionId,
            directoryScopeId = NormalizeDirectoryScope(directoryScopeId),
            justification,
            scheduleInfo = new
            {
                // Pass null (omitted from the JSON by WhenWritingNull) so
                // Graph defaults to "activate now". Sending DateTimeOffset.UtcNow
                // here causes intermittent 400s — by the time the request
                // lands on Graph's server, the timestamp is already in the
                // past by network-latency milliseconds, and Graph rejects
                // any startDateTime in the past. Matches legacy behaviour.
                startDateTime = (string?)null,
                expiration = new
                {
                    type = "afterDuration",
                    duration = FormatIso8601Duration(duration),
                },
            },
        };

        var created = await PostJsonAsync<EntraScheduleRequest>(
            "v1.0/roleManagement/directory/roleAssignmentScheduleRequests",
            body,
            cancellationToken);

        if (created is null)
        {
            throw new InvalidOperationException("Graph returned an empty body for self-activation.");
        }

        _logger.LogInformation(
            "Submitted self-activation {RequestId} for role {RoleId} at scope {DirectoryScopeId} on tenant {TenantId} ({Status}).",
            created.Id, roleDefinitionId, NormalizeDirectoryScope(directoryScopeId), _tenantId, created.Status);

        return created;
    }

    // Every eligibility carries a directoryScopeId, but a cache file written
    // before the plugin persisted it, or a response that omitted it, leaves the
    // caller with nothing — fall back to the directory-wide scope, which is what
    // the plugin sent unconditionally before.
    private static string NormalizeDirectoryScope(string? directoryScopeId)
        => string.IsNullOrWhiteSpace(directoryScopeId) ? DirectoryScope : directoryScopeId.Trim();

    public async Task<EntraScheduleRequest> DeactivateRoleAsync(
        string principalId,
        string roleDefinitionId,
        string? directoryScopeId,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefinitionId);

        // selfDeactivate is immediate — no scheduleInfo. Justification is
        // optional for deactivation; omit it (WhenWritingNull drops the null)
        // when the caller has none rather than sending an empty string.
        var body = new
        {
            action = "selfDeactivate",
            principalId,
            roleDefinitionId,
            directoryScopeId = NormalizeDirectoryScope(directoryScopeId),
            justification = string.IsNullOrWhiteSpace(justification) ? null : justification,
        };

        var created = await PostJsonAsync<EntraScheduleRequest>(
            "v1.0/roleManagement/directory/roleAssignmentScheduleRequests",
            body,
            cancellationToken);

        if (created is null)
        {
            throw new InvalidOperationException("Graph returned an empty body for self-deactivation.");
        }

        _logger.LogInformation(
            "Submitted self-deactivation {RequestId} for role {RoleId} on tenant {TenantId} ({Status}).",
            created.Id, roleDefinitionId, _tenantId, created.Status);

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

        var getUrl = $"beta/roleManagement/directory/roleAssignmentApprovals/{approvalId}?$expand=steps";
        var approval = await GetJsonAsync<EntraApproval>(getUrl, cancellationToken)
            ?? throw new InvalidOperationException($"Approval {approvalId} not found.");

        var openStep = approval.Steps?
            .FirstOrDefault(s => string.Equals(s.Status, "InProgress", StringComparison.OrdinalIgnoreCase));
        if (openStep?.Id is null)
        {
            throw new InvalidOperationException(
                $"Approval {approvalId} has no open step (already completed, or not assigned to you).");
        }

        var reviewResult = decision == ApprovalDecision.Approve ? "Approve" : "Deny";
        var patchUrl = $"beta/roleManagement/directory/roleAssignmentApprovals/{approvalId}/steps/{openStep.Id}";
        await PatchJsonAsync(patchUrl, new { reviewResult, justification }, cancellationToken);

        _logger.LogInformation(
            "{Decision} approval {ApprovalId} step {StepId} on tenant {TenantId}.",
            decision, approvalId, openStep.Id, _tenantId);
    }

    public async Task<string?> GetActivationStatusAsync(
        string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var url = $"v1.0/roleManagement/directory/roleAssignmentScheduleRequests/{requestId}?$select=id,status";
        var status = await GetJsonAsync<EntraScheduleRequestStatus>(url, cancellationToken);
        return status?.Status;
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task PatchJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string firstUrl, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        string? next = firstUrl;
        while (!string.IsNullOrEmpty(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(request, cancellationToken);
            await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync<ODataPage<T>>(JsonOptions, cancellationToken);
            if (page?.Value is not null)
            {
                results.AddRange(page.Value);
            }
            next = NormalizeNextLink(page?.NextLink);
        }
        return results;
    }

    // Microsoft Graph returns rich error JSON on 4xx/5xx — code, message,
    // and an inner-error block. HttpResponseMessage.EnsureSuccessStatusCode
    // throws but discards the body, so we lose the only diagnostic the
    // service gives us. This helper preserves the body in the exception
    // Message so the catch site can surface it to the user / log.
    private static async Task EnsureSuccessOrThrowWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            body = "(body unreadable)";
        }

        // Trim body to a sane length so a misbehaving service can't blow
        // up a log line. Graph error JSON is small in practice.
        if (body.Length > 1500) body = body[..1500] + "…(truncated)";

        throw new HttpRequestException(
            $"Graph {response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} returned {(int)response.StatusCode} {response.ReasonPhrase}. Body: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _ctx.GetHttpClient(_tenantId).SendAsync(
            PluginHttpClientNames.Graph,
            _ctx.GraphScope,
            request,
            cancellationToken);

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
