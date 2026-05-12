using System.Threading;
using System.Threading.Tasks;
using Azure.Core;

namespace AzureTray.Auth;

public interface ICredentialFactory
{
    // Returns a serialized TokenCredential for the given tenant. Subsequent calls
    // for the same tenant return the same wrapped credential, so its semaphore is
    // shared across services that target that tenant.
    //
    // The returned credential is configured with DisableAutomaticAuthentication —
    // it serves cached tokens silently (including the broker's "default Windows
    // account" path), but throws AuthenticationRequiredException instead of
    // popping interactive UI when a refresh fails. Callers wanting an explicit
    // interactive sign-in should invoke SignInAsync.
    TokenCredential GetForTenant(string tenantId);

    // Drop any cached credential for this tenant so the next GetForTenant call
    // rebuilds (e.g. after the tenant's ClientId has been changed in the store).
    void Invalidate(string tenantId);

    // Performs an explicit, user-driven interactive sign-in for the tenant.
    // Uses the stored SignInEmail as LoginHint so the broker pre-fills the
    // right account. On success the resulting token lands in the per-tenant
    // MSAL cache so subsequent silent GetForTenant calls return immediately.
    // Throws on user cancellation / broker failure.
    Task SignInAsync(string tenantId, CancellationToken cancellationToken);
}
