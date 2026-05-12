using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.AppRegistration;

// Discovers existing app registrations in a tenant via Microsoft Graph.
// All operations are performed under the signed-in user's credential
// for the target tenant; the user needs delegated Application.Read.All
// (or stronger) for these queries to succeed.
public interface IAppRegistrationDiscovery
{
    Task<AppRegistrationInfo?> FindByDisplayNameAsync(
        string tenantId, string displayName, CancellationToken cancellationToken);

    // Prefix search by displayName. Used by Settings → Add Tenant to let
    // users pick from a list when they don't have the exact name memorized.
    // Capped at `top` results (Graph $top, max 100). Returns an empty list
    // if the prefix matches nothing.
    Task<IReadOnlyList<AppRegistrationInfo>> SearchByDisplayNameAsync(
        string tenantId, string displayNamePrefix, int top, CancellationToken cancellationToken);
}
