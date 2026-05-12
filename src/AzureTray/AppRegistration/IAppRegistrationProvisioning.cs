using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

// Provisions app registrations in a tenant: creates new ones end-to-end
// and ensures redirect URIs on existing ones. All operations run under
// the signed-in user's credential for the tenant. Creating an app
// registration requires Application Administrator or Cloud Application
// Administrator (or Global Administrator); creating its service
// principal and admin-consenting scopes requires the same.
public interface IAppRegistrationProvisioning
{
    // Creates a brand-new app registration: POST /applications with the
    // given displayName, single-tenant audience, and requiredResourceAccess
    // populated from `required`; POST /servicePrincipals; PATCH
    // publicClient.redirectUris to include the WAM broker URI; POST
    // /oauth2PermissionGrants per resource for admin consent. Returns the
    // new app info plus counts of what was granted.
    //
    // Throws on the underlying Graph errors; partial success (e.g., app
    // created but consent failed) is reported via the result fields so the
    // caller can decide whether to persist the new clientId regardless.
    Task<AppRegistrationCreateResult> CreateAsync(
        string tenantId,
        string displayName,
        IReadOnlyList<PluginPermissionRequirement> required,
        CancellationToken cancellationToken);

    // Ensures the WAM broker's required redirect URI
    // ("ms-appx-web://microsoft.aad.brokerplugin/{appId}") is present on
    // the app registration's publicClient.redirectUris. Without it, MSAL's
    // broker integration fails with "ApiContractViolation" on cold start,
    // forcing the user to re-authenticate via Settings each time.
    //
    // Returns true if the URI was added (or was already present); false if
    // the app registration could not be found. Throws on Graph errors.
    Task<bool> EnsureBrokerRedirectUriAsync(
        string tenantId,
        string appClientId,
        CancellationToken cancellationToken);
}
