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
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Graph;

internal sealed class GraphPimClient : IGraphPimClient
{
    private const string DirectoryScope = "/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPluginContext _ctx;
    private readonly ILogger _logger;

    public GraphPimClient(IPluginContext ctx)
    {
        _ctx = ctx;
        _logger = ctx.Logger;
    }

    public async Task<string?> GetSignedInUserIdAsync(string tenantId, CancellationToken cancellationToken)
    {
        var me = await GetJsonAsync<EntraMe>(tenantId, "v1.0/me?$select=id", cancellationToken);
        return me?.Id;
    }

    public async Task<IReadOnlyList<EntraEligibilitySchedule>> ListActiveRoleAssignmentsAsync(
        string tenantId, string principalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url =
            "v1.0/roleManagement/directory/roleAssignmentScheduleInstances" +
            $"?$filter=principalId eq '{principalId}'" +
            "&$expand=roleDefinition";

        return await GetAllPagesAsync<EntraEligibilitySchedule>(tenantId, url, cancellationToken);
    }

    public async Task<IReadOnlyList<EntraEligibilitySchedule>> ListEligibleRolesAsync(
        string tenantId, string principalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(principalId);

        var url =
            "v1.0/roleManagement/directory/roleEligibilitySchedules" +
            $"?$filter=principalId eq '{principalId}'" +
            "&$expand=roleDefinition,principal";

        return await GetAllPagesAsync<EntraEligibilitySchedule>(tenantId, url, cancellationToken);
    }

    public async Task<IReadOnlyList<EntraScheduleRequest>> ListPendingApprovalsAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        var url =
            "v1.0/roleManagement/directory/roleAssignmentScheduleRequests" +
            "?$filter=status eq 'PendingApproval'" +
            "&$expand=principal,roleDefinition";

        return await GetAllPagesAsync<EntraScheduleRequest>(tenantId, url, cancellationToken);
    }

    public async Task<bool?> CheckApprovalRequiredAsync(
        string tenantId, string roleDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefinitionId);

        var assignmentUrl =
            "v1.0/policies/roleManagementPolicyAssignments" +
            $"?$filter=scopeId eq '/' and scopeType eq 'Directory' and roleDefinitionId eq '{roleDefinitionId}'";

        var assignments = await GetAllPagesAsync<EntraPolicyAssignment>(tenantId, assignmentUrl, cancellationToken);
        var policyId = assignments.FirstOrDefault()?.PolicyId;
        if (string.IsNullOrWhiteSpace(policyId))
        {
            _logger.LogDebug("No policy assignment found for role {RoleId} in tenant {TenantId}.", roleDefinitionId, tenantId);
            return null;
        }

        var ruleUrl = $"v1.0/policies/roleManagementPolicies/{policyId}/rules/Approval_EndUser_Assignment";
        var rule = await GetJsonAsync<EntraApprovalRule>(tenantId, ruleUrl, cancellationToken);
        return rule?.Setting?.IsApprovalRequired;
    }

    public async Task<EntraScheduleRequest> ActivateRoleAsync(
        string tenantId,
        string principalId,
        string roleDefinitionId,
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
            directoryScopeId = DirectoryScope,
            justification,
            scheduleInfo = new
            {
                startDateTime = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                expiration = new
                {
                    type = "afterDuration",
                    duration = FormatIso8601Duration(duration),
                },
            },
        };

        var created = await PostJsonAsync<EntraScheduleRequest>(
            tenantId,
            "v1.0/roleManagement/directory/roleAssignmentScheduleRequests",
            body,
            cancellationToken);

        if (created is null)
        {
            throw new InvalidOperationException("Graph returned an empty body for self-activation.");
        }

        _logger.LogInformation(
            "Submitted self-activation {RequestId} for role {RoleId} on tenant {TenantId} ({Status}).",
            created.Id, roleDefinitionId, tenantId, created.Status);

        return created;
    }

    public async Task ReviewAsync(
        string tenantId,
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        var getUrl = $"beta/roleManagement/directory/roleAssignmentApprovals/{approvalId}?$expand=steps";
        var approval = await GetJsonAsync<EntraApproval>(tenantId, getUrl, cancellationToken)
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
        await PatchJsonAsync(tenantId, patchUrl, new { reviewResult, justification }, cancellationToken);

        _logger.LogInformation(
            "{Decision} approval {ApprovalId} step {StepId} on tenant {TenantId}.",
            decision, approvalId, openStep.Id, tenantId);
    }

    public async Task<string?> GetActivationStatusAsync(
        string tenantId, string requestId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        var url = $"v1.0/roleManagement/directory/roleAssignmentScheduleRequests/{requestId}?$select=id,status";
        var status = await GetJsonAsync<EntraScheduleRequestStatus>(tenantId, url, cancellationToken);
        return status?.Status;
    }

    private async Task<T?> GetJsonAsync<T>(string tenantId, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(tenantId, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task<T?> PostJsonAsync<T>(string tenantId, string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(tenantId, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    private async Task PatchJsonAsync(string tenantId, string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(tenantId, request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<List<T>> GetAllPagesAsync<T>(string tenantId, string firstUrl, CancellationToken cancellationToken)
    {
        var results = new List<T>();
        string? next = firstUrl;
        while (!string.IsNullOrEmpty(next))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            using var response = await SendAsync(tenantId, request, cancellationToken);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<ODataPage<T>>(JsonOptions, cancellationToken);
            if (page?.Value is not null)
            {
                results.AddRange(page.Value);
            }
            next = NormalizeNextLink(page?.NextLink);
        }
        return results;
    }

    private Task<HttpResponseMessage> SendAsync(string tenantId, HttpRequestMessage request, CancellationToken cancellationToken)
        => _ctx.Http.SendAsync(
            PluginHttpClientNames.Graph,
            tenantId,
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
