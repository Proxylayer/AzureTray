using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging;
using AzureTray.Auth;
using AzureTray.AzureCloud;

namespace AzureTray.Graph;

public interface IGraphOrganizationClient
{
    // Resolves a user-friendly display name for the tenant ("Contoso Inc.")
    // for use in Settings and the tray menu. Returns null only when every
    // fallback fails — the caller can then default to UPN / domain.
    Task<string?> GetDisplayNameAsync(string tenantId, CancellationToken cancellationToken);
}

public sealed class GraphOrganizationClient : IGraphOrganizationClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ICredentialFactory _credentials;
    private readonly IAzureCloudConfig _cloud;
    private readonly ILogger<GraphOrganizationClient> _logger;

    public GraphOrganizationClient(
        IHttpClientFactory httpFactory,
        ICredentialFactory credentials,
        IAzureCloudConfig cloud,
        ILogger<GraphOrganizationClient> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _cloud = cloud;
        _logger = logger;
    }

    public async Task<string?> GetDisplayNameAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        _logger.LogInformation(
            "Calling Graph /v1.0/organization to resolve display name for tenant {TenantId}.",
            tenantId);

        // First choice: /v1.0/organization — the canonical tenant display
        // name. Requires Directory.Read.All / Organization.Read.All which
        // Azure CLI's public client often does NOT have pre-consented, so
        // this returns 403 in many tenants.
        var fromOrg = await TryGetOrganizationDisplayNameAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromOrg))
        {
            _logger.LogInformation(
                "Graph /organization returned tenant display name {Name} for {TenantId}.",
                fromOrg, tenantId);
            return fromOrg;
        }

        _logger.LogInformation(
            "Graph /organization yielded no display name for tenant {TenantId}; trying /me companyName.",
            tenantId);

        // Fallback: /v1.0/me?$select=companyName. User.Read is always
        // consented for public clients, so this almost always succeeds and
        // returns the signed-in user's company — which matches the tenant
        // name in virtually all corporate Entra setups.
        var fromMe = await TryGetCompanyNameFromMeAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(fromMe))
        {
            _logger.LogInformation(
                "Graph /me companyName returned {Name} for tenant {TenantId}.",
                fromMe, tenantId);
            return fromMe;
        }

        _logger.LogWarning(
            "Could not resolve a tenant display name for {TenantId} from Graph; caller will fall back.",
            tenantId);
        return null;
    }

    private async Task<string?> TryGetOrganizationDisplayNameAsync(string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendGraphGetAsync(tenantId, "v1.0/organization?$select=displayName", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Graph /v1.0/organization returned HTTP {Status} for tenant {TenantId}. Falling back to /me companyName. (Azure CLI public client often lacks Directory.Read.All admin consent.)",
                    (int)response.StatusCode, tenantId);
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<OrganizationListResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            var name = payload?.Value is { Count: > 0 } orgs ? orgs[0]?.DisplayName : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                _logger.LogWarning("Graph /v1.0/organization returned no displayName for tenant {TenantId}.", tenantId);
                return null;
            }
            return name;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Graph /v1.0/organization request threw for tenant {TenantId}; falling back to /me companyName.",
                tenantId);
            return null;
        }
    }

    private async Task<string?> TryGetCompanyNameFromMeAsync(string tenantId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendGraphGetAsync(tenantId, "v1.0/me?$select=companyName", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Graph /v1.0/me companyName returned HTTP {Status} for tenant {TenantId}.",
                    (int)response.StatusCode, tenantId);
                return null;
            }

            var payload = await response.Content
                .ReadFromJsonAsync<MeCompanyResponse>(JsonOptions, cancellationToken)
                .ConfigureAwait(false);

            return string.IsNullOrWhiteSpace(payload?.CompanyName) ? null : payload.CompanyName;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Graph /v1.0/me companyName request threw for tenant {TenantId}.",
                tenantId);
            return null;
        }
    }

    private async Task<HttpResponseMessage> SendGraphGetAsync(string tenantId, string relativeUrl, CancellationToken cancellationToken)
    {
        var credential = _credentials.GetForTenant(tenantId);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { _cloud.GraphScope }),
            cancellationToken).ConfigureAwait(false);

        var client = _httpFactory.CreateClient(HttpClientNames.Graph);
        var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private sealed class OrganizationListResponse
    {
        [JsonPropertyName("value")]
        public List<OrganizationEntry>? Value { get; init; }
    }

    private sealed class OrganizationEntry
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }
    }

    private sealed class MeCompanyResponse
    {
        [JsonPropertyName("companyName")]
        public string? CompanyName { get; init; }
    }
}
