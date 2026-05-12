using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.AzureCloud;

namespace AzureTray.Auth;

public interface IOpenIdConfigClient
{
    // Resolves a domain (e.g. "contoso.com" or "contoso.onmicrosoft.com") to
    // its Entra tenant via the OIDC discovery document. Returns null when the
    // domain is not associated with an Entra tenant.
    Task<TenantDiscoveryResult?> DiscoverAsync(string domain, CancellationToken cancellationToken);
}

public sealed record TenantDiscoveryResult(
    string TenantId,
    string Issuer,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? CloudInstanceName,
    string? TenantRegionScope);

public sealed class OpenIdConfigClient : IOpenIdConfigClient
{
    // OIDC discovery is unauthenticated and lives off the same authority as
    // sign-in. Using IAzureCloudConfig keeps us aligned with sovereign clouds
    // if the user is pointed at one.
    private readonly IAzureCloudConfig _cloud;
    private readonly ILogger<OpenIdConfigClient> _logger;
    private readonly HttpClient _http;

    public OpenIdConfigClient(IHttpClientFactory factory, IAzureCloudConfig cloud, ILogger<OpenIdConfigClient> logger)
    {
        _cloud = cloud;
        _logger = logger;
        _http = factory.CreateClient(nameof(OpenIdConfigClient));
    }

    public async Task<TenantDiscoveryResult?> DiscoverAsync(string domain, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);

        var url = new Uri(_cloud.Authority, $"{Uri.EscapeDataString(domain.Trim())}/v2.0/.well-known/openid-configuration");

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest)
        {
            _logger.LogDebug("OIDC discovery for {Domain} returned {Status}; tenant not found.", domain, (int)response.StatusCode);
            return null;
        }
        response.EnsureSuccessStatusCode();

        var doc = await response.Content.ReadFromJsonAsync<OpenIdConfigurationDocument>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (doc?.Issuer is null) return null;

        var tenantId = ExtractTenantIdFromIssuer(doc.Issuer);
        if (string.IsNullOrEmpty(tenantId)) return null;

        return new TenantDiscoveryResult(
            TenantId: tenantId,
            Issuer: doc.Issuer,
            AuthorizationEndpoint: doc.AuthorizationEndpoint ?? string.Empty,
            TokenEndpoint: doc.TokenEndpoint ?? string.Empty,
            CloudInstanceName: doc.CloudInstanceName,
            TenantRegionScope: doc.TenantRegionScope);
    }

    // Issuer format: "https://login.microsoftonline.com/{tenantGuid}/v2.0"
    private static string? ExtractTenantIdFromIssuer(string issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri)) return null;
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        return Guid.TryParse(segments[0], out var guid) ? guid.ToString() : null;
    }

    private sealed class OpenIdConfigurationDocument
    {
        [JsonPropertyName("issuer")]
        public string? Issuer { get; init; }

        [JsonPropertyName("authorization_endpoint")]
        public string? AuthorizationEndpoint { get; init; }

        [JsonPropertyName("token_endpoint")]
        public string? TokenEndpoint { get; init; }

        [JsonPropertyName("cloud_instance_name")]
        public string? CloudInstanceName { get; init; }

        [JsonPropertyName("tenant_region_scope")]
        public string? TenantRegionScope { get; init; }
    }
}
