using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration.Internal;

// Shared HTTP/JSON plumbing for the AppRegistration services. Wraps an
// HttpClientFactory + ICredentialFactory so each service is reduced to
// its own domain logic. Single point that knows which named HTTP client
// to use, which scope to request, and how to authenticate the request.
public sealed class AppRegistrationGraphClient
{
    // Well-known Microsoft Graph and Azure RM appIds — constant across all Entra tenants.
    public const string GraphResourceAppId = "00000003-0000-0000-c000-000000000000";
    public const string ArmResourceAppId = "797f4846-ba00-4fd7-ba43-dac1f8f63013";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ICredentialFactory _credentials;
    private readonly IAzureCloudConfig _cloud;

    public AppRegistrationGraphClient(
        IHttpClientFactory httpFactory,
        ICredentialFactory credentials,
        IAzureCloudConfig cloud)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _cloud = cloud;
    }

    public async Task<T?> GetAsync<T>(string tenantId, string url, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Get, url, body: null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    public async Task<List<T>> ListAsync<T>(string tenantId, string firstUrl, CancellationToken ct)
    {
        var results = new List<T>();
        string? next = firstUrl;
        while (next is not null)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetAsync<ODataPage<T>>(tenantId, next, ct);
            if (page?.Value is not null) results.AddRange(page.Value);
            next = NormalizeNextLink(page?.NextLink);
        }
        return results;
    }

    public async Task<T?> PostAsync<T>(string tenantId, string url, object body, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Post, url, body, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions, ct);
    }

    public async Task PatchAsync(string tenantId, string url, object body, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Patch, url, body, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteAsync(string tenantId, string url, CancellationToken ct)
    {
        using var response = await SendAsync(tenantId, HttpMethod.Delete, url, body: null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task<HttpResponseMessage> SendAsync(
        string tenantId, HttpMethod method, string url, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        return await SendAsync(tenantId, request, ct);
    }

    // Overload taking a pre-built request so callers can add headers Graph
    // requires for advanced queries (e.g. ConsistencyLevel: eventual for
    // startsWith on /applications).
    public async Task<HttpResponseMessage> SendAsync(
        string tenantId, HttpRequestMessage request, CancellationToken ct)
    {
        var credential = _credentials.GetForTenant(tenantId);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([_cloud.GraphScope]),
            ct);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        var client = _httpFactory.CreateClient(HttpClientNames.Graph);
        return await client.SendAsync(request, ct);
    }

    internal async Task<GraphApplication?> GetAppByClientIdAsync(
        string tenantId, string appClientId, CancellationToken ct)
    {
        var url = $"v1.0/applications?$filter=appId eq '{EscapeFilter(appClientId)}'&$select=id,appId,displayName,requiredResourceAccess&$top=1";
        var page = await GetAsync<ODataPage<GraphApplication>>(tenantId, url, ct);
        return page?.Value?.FirstOrDefault();
    }

    internal async Task<GraphServicePrincipal?> GetServicePrincipalByAppIdAsync(
        string tenantId, string appId, CancellationToken ct)
    {
        var url = $"v1.0/servicePrincipals?$filter=appId eq '{EscapeFilter(appId)}'&$select=id,appId,displayName&$top=1";
        var page = await GetAsync<ODataPage<GraphServicePrincipal>>(tenantId, url, ct);
        return page?.Value?.FirstOrDefault();
    }

    // Lookup a service principal by its object id (used to translate the
    // resourceId on an oauth2PermissionGrant back to a resource appId).
    internal async Task<GraphServicePrincipal?> GetServicePrincipalByObjectIdAsync(
        string tenantId, string objectId, CancellationToken ct)
    {
        var url = $"v1.0/servicePrincipals/{objectId}?$select=id,appId,displayName";
        try
        {
            return await GetAsync<GraphServicePrincipal>(tenantId, url, ct);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    internal static AppRegistrationInfo? ToInfo(GraphApplication? app)
        => app?.Id is not null && app.AppId is not null
            ? new AppRegistrationInfo(app.Id, app.AppId, app.DisplayName ?? string.Empty)
            : null;

    // OData $filter string values must escape single quotes by doubling them.
    public static string EscapeFilter(string value) => value.Replace("'", "''");

    public static string GetResourceAppId(PermissionApi api) => api switch
    {
        PermissionApi.MicrosoftGraph => GraphResourceAppId,
        PermissionApi.AzureResourceManager => ArmResourceAppId,
        _ => throw new ArgumentOutOfRangeException(nameof(api), api, "Unsupported PermissionApi."),
    };

    private static string? NormalizeNextLink(string? nextLink)
    {
        if (string.IsNullOrWhiteSpace(nextLink)) return null;
        if (Uri.TryCreate(nextLink, UriKind.Absolute, out var absolute))
        {
            return absolute.PathAndQuery.TrimStart('/');
        }
        return nextLink;
    }
}
