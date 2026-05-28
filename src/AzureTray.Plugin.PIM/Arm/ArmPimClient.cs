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
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Graph;

namespace AzureTray.Plugin.PIM.Arm;

internal sealed class ArmPimClient : IArmPimClient
{
    private const string SubscriptionsApi = "2022-12-01";
    private const string AuthorizationApi = "2020-10-01";
    private const string ApprovalApi = "2021-01-01-preview";

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

    public async Task<bool?> CheckApprovalRequiredAsync(
        string scope, string roleDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(roleDefinitionId);

        var prefix = NormalizeScope(scope);
        var assignmentsUrl =
            $"{prefix}providers/Microsoft.Authorization/roleManagementPolicyAssignments" +
            $"?api-version={AuthorizationApi}";

        var assignments = await GetAllPagesAsync<ArmPolicyAssignment>(assignmentsUrl, cancellationToken);
        var match = assignments.FirstOrDefault(a =>
            string.Equals(a.Properties?.RoleDefinitionId, roleDefinitionId, StringComparison.OrdinalIgnoreCase));

        var policyId = match?.Properties?.PolicyId;
        if (string.IsNullOrWhiteSpace(policyId))
        {
            _logger.LogDebug(
                "No ARM policy assignment found for role {RoleId} at {Scope} (tenant {TenantId}).",
                roleDefinitionId, scope, _tenantId);
            return null;
        }

        var policyUrl = $"{policyId!.TrimStart('/')}?api-version={AuthorizationApi}";
        var policy = await GetJsonAsync<ArmPolicyResponse>(policyUrl, cancellationToken);

        var approvalRule = policy?.Properties?.Rules?
            .FirstOrDefault(r => string.Equals(r.RuleType, "RoleManagementPolicyApprovalRule", StringComparison.OrdinalIgnoreCase));

        return approvalRule?.Setting?.IsApprovalRequired;
    }

    public async Task<ArmRoleAssignmentScheduleRequest> ActivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string linkedRoleEligibilityScheduleId,
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
        var prefix = NormalizeScope(scope);
        var url =
            $"{prefix}providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{requestId}" +
            $"?api-version={AuthorizationApi}";

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

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<ArmRoleAssignmentScheduleRequest>(JsonOptions, cancellationToken);
        if (created is null)
        {
            throw new InvalidOperationException("ARM returned an empty body for self-activation.");
        }

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
        var prefix = NormalizeScope(scope);
        var url =
            $"{prefix}providers/Microsoft.Authorization/roleAssignmentScheduleRequests/{requestId}" +
            $"?api-version={AuthorizationApi}";

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

        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);

        var created = await response.Content.ReadFromJsonAsync<ArmRoleAssignmentScheduleRequest>(JsonOptions, cancellationToken);
        if (created is null)
        {
            throw new InvalidOperationException("ARM returned an empty body for self-deactivation.");
        }

        _logger.LogInformation(
            "Submitted ARM self-deactivation {RequestId} for role {RoleId} at {Scope} (tenant {TenantId}, status {Status}).",
            requestId, roleDefinitionId, scope, _tenantId, created.Properties?.Status);

        return created;
    }

    public async Task ReviewAsync(
        string scope,
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(approvalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(justification);

        var prefix = NormalizeScope(scope);

        var approvalUrl =
            $"{prefix}providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}" +
            $"?api-version={ApprovalApi}";
        var approval = await GetJsonAsync<ArmApproval>(approvalUrl, cancellationToken)
            ?? throw new InvalidOperationException($"ARM approval {approvalId} not found at scope {scope}.");

        var openStage = approval.Properties?.Stages?
            .FirstOrDefault(s => string.Equals(s.Properties?.Status, "InProgress", StringComparison.OrdinalIgnoreCase));
        if (openStage?.Id is null || openStage.Name is null)
        {
            throw new InvalidOperationException(
                $"ARM approval {approvalId} has no open stage (already completed, or not assigned to you).");
        }

        var reviewResult = decision == ApprovalDecision.Approve ? "Approve" : "Deny";
        var stageUrl =
            $"{prefix}providers/Microsoft.Authorization/roleAssignmentApprovals/{approvalId}/stages/{openStage.Name}" +
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
            "{Decision} ARM approval {ApprovalId} stage {StageId} at {Scope} (tenant {TenantId}).",
            decision, approvalId, openStage.Name, scope, _tenantId);
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
        var distinct = scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
        if (distinct.Count == 0) return Array.Empty<T>();

        var combined = new List<T>();
        foreach (var batch in distinct.Chunk(FanOutBatchSize))
        {
            var tasks = batch.Select(scope =>
            {
                var url = urlForScope(NormalizeScope(scope));
                return GetAllPagesAsync<T>(url, cancellationToken);
            });
            foreach (var page in await Task.WhenAll(tasks).ConfigureAwait(false))
            {
                combined.AddRange(page);
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

        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            body = "(body unreadable)";
        }
        if (body.Length > 1500) body = body[..1500] + "…(truncated)";

        throw new HttpRequestException(
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
