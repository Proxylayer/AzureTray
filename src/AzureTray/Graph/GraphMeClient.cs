using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Microsoft.Extensions.Logging;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Dto;

namespace AzureTray.Graph;

// Sole purpose: verify a tenant's credentials work by calling Graph /me.
// Used by the add-tenant flow in Settings. Anything richer (PIM, etc.) lives
// in a plugin that talks to Graph via IPluginContext.Http.
public sealed class GraphMeClient : IGraphMeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly ICredentialFactory _credentials;
    private readonly IAzureCloudConfig _cloud;
    private readonly ILogger<GraphMeClient> _logger;

    public GraphMeClient(
        IHttpClientFactory httpFactory,
        ICredentialFactory credentials,
        IAzureCloudConfig cloud,
        ILogger<GraphMeClient> logger)
    {
        _httpFactory = httpFactory;
        _credentials = credentials;
        _cloud = cloud;
        _logger = logger;
    }

    public async Task<MeResponse> GetMeAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        var credential = _credentials.GetForTenant(tenantId);
        var token = await credential.GetTokenAsync(
            new TokenRequestContext([_cloud.GraphScope]),
            cancellationToken);

        var client = _httpFactory.CreateClient(HttpClientNames.Graph);
        using var request = new HttpRequestMessage(HttpMethod.Get, "v1.0/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var me = await response.Content.ReadFromJsonAsync<MeResponse>(JsonOptions, cancellationToken);
        if (me is null)
        {
            throw new InvalidOperationException("Graph /me returned an empty response.");
        }

        _logger.LogInformation(
            "Resolved Graph /me for tenant {TenantId}: {Upn}.",
            tenantId, me.UserPrincipalName);

        return me;
    }
}
