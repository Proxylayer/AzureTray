using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Authenticated HTTP egress for plugins. The host owns the named client
/// (with resilience handlers), the per-tenant credential, and cloud endpoint
/// resolution. Plugins supply the request shape and receive the raw response.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Security — SSRF:</strong> never forward a URL from user input or
/// an untrusted API response without validating the scheme (<c>https://</c>)
/// and host against an expected allowlist.
/// </para>
/// <para>
/// <strong>Security — minimal scope:</strong> request the narrowest OAuth
/// scope that satisfies the call. The host tokens are scoped to exactly what
/// you supply; over-broad scopes leak privileges if responses are ever logged.
/// </para>
/// </remarks>
public interface IPluginHttpClient
{
    /// <summary>
    /// Sends an authenticated HTTP request using a host-managed named client.
    /// The tenant this client is scoped to is fixed at the time it is obtained
    /// from <see cref="IPluginContext.GetHttpClient"/> — you cannot override it
    /// here. This is intentional: a per-tenant client can only acquire tokens
    /// for its own tenant.
    /// </summary>
    /// <param name="clientName">
    /// Named client registered in the host.
    /// Use <see cref="PluginHttpClientNames.Graph"/> or
    /// <see cref="PluginHttpClientNames.Arm"/> for standard Microsoft APIs.
    /// </param>
    /// <param name="scope">
    /// OAuth scope for token acquisition, e.g.
    /// <see cref="IPluginContext.GraphScope"/>,
    /// <see cref="IPluginContext.ArmScope"/>, or a custom app scope such as
    /// <c>"api://my-backend/.default"</c>.
    /// </param>
    /// <param name="request">
    /// HTTP request message with an absolute URI. The host does not apply a
    /// base-address override for plugin calls.
    /// </param>
    /// <param name="cancellationToken">Propagate plugin shutdown or timeout.</param>
    /// <returns>
    /// The raw <see cref="System.Net.Http.HttpResponseMessage"/>. The caller
    /// must check <see cref="System.Net.Http.HttpResponseMessage.IsSuccessStatusCode"/>.
    /// </returns>
    Task<System.Net.Http.HttpResponseMessage> SendAsync(
        string clientName,
        string scope,
        System.Net.Http.HttpRequestMessage request,
        CancellationToken cancellationToken);
}
