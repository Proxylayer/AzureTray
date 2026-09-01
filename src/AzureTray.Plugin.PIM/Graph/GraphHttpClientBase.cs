using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Http;

namespace AzureTray.Plugin.PIM.Graph;

// Microsoft Graph specifics on top of the transport shared with ARM. Extracted
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
internal abstract class GraphHttpClientBase : PimHttpClientBase
{
    protected GraphHttpClientBase(IPluginContext ctx, string tenantId)
        : base(ctx, tenantId, PluginHttpClientNames.Graph, () => ctx.GraphScope, "Graph")
    {
    }

    protected override async Task<IPageEnvelope<T>?> ReadPageAsync<T>(string url, CancellationToken cancellationToken)
        => await GetJsonAsync<ODataPage<T>>(url, cancellationToken).ConfigureAwait(false);

    // True when Graph rejected the request itself (bad query, missing scope,
    // absent resource) rather than failing to serve it. Retrying the identical
    // request is pointless; retrying a *different* one may not be, which is
    // what the optional-$expand probes rely on.
    protected static bool IsClientError(HttpRequestException ex)
        => ex.StatusCode is { } status && (int)status >= 400 && (int)status < 500;
}
