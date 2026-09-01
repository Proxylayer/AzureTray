using System;
using System.Collections.Generic;
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
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Graph;

// Microsoft Graph transport shared by every Graph-backed PIM client. Extracted
// when PIM for Groups arrived: directory roles
// (roleManagement/directory/...), group memberships and ownerships
// (identityGovernance/privilegedAccess/group/...) and the tenant's role
// management policies are three different resource families that happen to
// share one transport, one error contract, one paging shape, and — because the
// policy rule ids are identical across resource types — one policy-rule reader.
//
// Only the plumbing lives here. Anything that knows a URL, a request body, or
// what a resource means belongs in the derived client, so the split stays
// "how we talk to Graph" versus "what we ask it for".
internal abstract class GraphHttpClientBase
{
    // Rule ids inside a unifiedRoleManagementPolicy. They are the same strings
    // for directory roles and for PIM for Groups — the policy resource is
    // shared, only the scopeType on the assignment differs — so the readers
    // below serve both.
    protected const string EndUserExpirationRuleId = "Expiration_EndUser_Assignment";
    protected const string EndUserApprovalRuleId = "Approval_EndUser_Assignment";

    // Camel-case out, case-insensitive in. Graph's schema documents camelCase
    // enum values but live payloads return PascalCase for several of them
    // ("Direct" vs "direct", "Assigned" vs "assigned", "SelfActivate" vs
    // "selfActivate"), so a case-sensitive read silently loses the value.
    // WhenWritingNull is what lets a request body model an omitted property as
    // a null literal in the anonymous type.
    protected static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IPluginContext _ctx;

    protected GraphHttpClientBase(IPluginContext ctx, string tenantId)
    {
        _ctx = ctx;
        Logger = ctx.Logger;
        TenantId = tenantId;
    }

    protected ILogger Logger { get; }

    protected string TenantId { get; }

    protected async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    protected async Task<T?> PostJsonAsync<T>(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
    }

    protected async Task PatchJsonAsync(string url, object body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await SendAsync(request, cancellationToken);
        await EnsureSuccessOrThrowWithBodyAsync(response, cancellationToken).ConfigureAwait(false);
    }

    protected async Task<List<T>> GetAllPagesAsync<T>(string firstUrl, CancellationToken cancellationToken)
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
    protected static async Task EnsureSuccessOrThrowWithBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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

    // True when Graph rejected the request itself (bad query, missing scope,
    // absent resource) rather than failing to serve it. Retrying the identical
    // request is pointless; retrying a *different* one may not be, which is
    // what the optional-$expand probes rely on.
    protected static bool IsClientError(HttpRequestException ex)
        => ex.StatusCode is { } status && (int)status >= 400 && (int)status < 500;

    protected static bool HasStatus(HttpRequestException ex, HttpStatusCode status)
        => ex.StatusCode == status;

    private Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => _ctx.GetHttpClient(TenantId).SendAsync(
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

    // Expiration_EndUser_Assignment is the only rule that governs a user
    // self-activating an eligible role or group access. The other expiration
    // rules (Expiration_Admin_Eligibility, Expiration_Admin_Assignment) are
    // days-scale admin caps and must never stand in for it.
    protected static TimeSpan? ReadMaxActivationDuration(List<EntraPolicyRule> rules)
    {
        var rule = rules.FirstOrDefault(r =>
            string.Equals(r.Id, EndUserExpirationRuleId, StringComparison.OrdinalIgnoreCase)
            && IsRuleType(r.ODataType, "unifiedRoleManagementPolicyExpirationRule"));

        return Iso8601Duration.TryParse(rule?.MaximumDuration);
    }

    protected static bool? ReadApprovalRequired(List<EntraPolicyRule> rules)
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

    protected static string FormatIso8601Duration(TimeSpan duration)
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
