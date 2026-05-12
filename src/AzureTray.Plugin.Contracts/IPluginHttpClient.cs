using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

// Authenticated HTTP egress for plugins. The host owns the named client (with
// resilience handler attached), the per-tenant TokenCredential, and the cloud
// endpoint configuration. The plugin just supplies the request shape and asks.
//
// clientName: matches the host-configured named client. Use the standard
//             values exposed by PluginHttpClientNames ("graph", "arm").
// tenantId:   tenant the call is scoped to; the host fetches its credential.
// scope:      OAuth scope for the call, e.g. context.GraphScope or context.ArmScope.
public interface IPluginHttpClient
{
    Task<HttpResponseMessage> SendAsync(
        string clientName,
        string tenantId,
        string scope,
        HttpRequestMessage request,
        CancellationToken cancellationToken);
}
