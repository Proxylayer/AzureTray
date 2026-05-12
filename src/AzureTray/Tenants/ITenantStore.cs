using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Models;

namespace AzureTray.Tenants;

// Persistent list of tenants the user has added. State is held in memory and
// flushed to disk on every mutation. Reads are synchronous (cheap snapshot);
// writes are async so the I/O path can be cancelled cleanly.
public interface ITenantStore
{
    IReadOnlyList<Tenant> GetAll();

    Tenant? FindByTenantId(string tenantId);

    Task AddOrUpdateAsync(Tenant tenant, CancellationToken cancellationToken);

    Task RemoveAsync(string tenantId, CancellationToken cancellationToken);
}
