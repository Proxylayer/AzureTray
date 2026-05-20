using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugins;

/// <summary>
/// Host-internal contract for authenticated plugin HTTP egress.
/// Exposes the full per-tenant <c>SendAsync</c> signature used by
/// <see cref="TenantScopedPluginHttpClient"/> to bake a tenant into each
/// client instance it hands to plugins. Plugins never see this interface —
/// they receive an <see cref="AzureTray.Plugin.Contracts.IPluginHttpClient"/>
/// obtained from <see cref="AzureTray.Plugin.Contracts.IPluginContext.GetHttpClient"/>.
/// </summary>
internal interface IPluginHttpClientCore
{
    Task<HttpResponseMessage> SendCoreAsync(
        string clientName,
        string tenantId,
        string scope,
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}
