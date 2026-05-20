using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugins;

/// <summary>
/// Wraps <see cref="IPluginHttpClientCore"/> with a fixed tenant so that
/// plugins physically cannot request tokens for any tenant other than the one
/// they were scoped to when <see cref="IPluginContext.GetHttpClient"/> was called.
/// </summary>
internal sealed class TenantScopedPluginHttpClient : IPluginHttpClient
{
    private readonly IPluginHttpClientCore _core;
    private readonly string _tenantId;

    internal TenantScopedPluginHttpClient(IPluginHttpClientCore core, string tenantId)
    {
        _core = core;
        _tenantId = tenantId;
    }

    public Task<HttpResponseMessage> SendAsync(
        string clientName,
        string scope,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
        => _core.SendCoreAsync(clientName, _tenantId, scope, request, cancellationToken);
}
