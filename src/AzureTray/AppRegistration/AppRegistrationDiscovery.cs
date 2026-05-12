using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.AppRegistration.Internal;

namespace AzureTray.AppRegistration;

public sealed class AppRegistrationDiscovery : IAppRegistrationDiscovery
{
    private readonly AppRegistrationGraphClient _graph;

    public AppRegistrationDiscovery(AppRegistrationGraphClient graph)
    {
        _graph = graph;
    }

    public async Task<AppRegistrationInfo?> FindByDisplayNameAsync(
        string tenantId, string displayName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        var url = $"v1.0/applications?$filter=displayName eq '{AppRegistrationGraphClient.EscapeFilter(displayName)}'&$select=id,appId,displayName&$top=1";
        var page = await _graph.GetAsync<ODataPage<GraphApplication>>(tenantId, url, cancellationToken);
        return AppRegistrationGraphClient.ToInfo(page?.Value?.FirstOrDefault());
    }

    public async Task<IReadOnlyList<AppRegistrationInfo>> SearchByDisplayNameAsync(
        string tenantId, string displayNamePrefix, int top, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNamePrefix);
        var clampedTop = Math.Clamp(top, 1, 100);

        // Graph requires ConsistencyLevel: eventual + $count for startsWith on
        // applications. Without it, the API returns 400.
        var url = $"v1.0/applications?$filter=startsWith(displayName,'{AppRegistrationGraphClient.EscapeFilter(displayNamePrefix)}')&$orderby=displayName&$select=id,appId,displayName&$top={clampedTop}&$count=true";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("ConsistencyLevel", "eventual");
        using var response = await _graph.SendAsync(tenantId, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var page = await response.Content.ReadFromJsonAsync<ODataPage<GraphApplication>>(
            AppRegistrationGraphClient.JsonOptions, cancellationToken);

        var results = new List<AppRegistrationInfo>();
        if (page?.Value is { } apps)
        {
            foreach (var app in apps)
            {
                var info = AppRegistrationGraphClient.ToInfo(app);
                if (info is not null) results.Add(info);
            }
        }
        return results;
    }
}
