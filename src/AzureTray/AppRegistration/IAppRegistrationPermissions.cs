using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

// Checks and applies delegated permission requirements on an existing
// app registration. Operations run under the signed-in user's credential
// for the target tenant; Global/Application Administrator authority is
// required for Ensure to succeed.
public interface IAppRegistrationPermissions
{
    Task<PermissionCheckResult> CheckAsync(
        string tenantId,
        string appClientId,
        IReadOnlyList<PluginPermissionRequirement> required,
        CancellationToken cancellationToken);

    // `required` is what gets provisioned: every well-formed declaration the
    // host and the loaded plugins made. `unprovisionable` is what was thrown
    // out on the way here — declarations whose ScopeId is not a GUID.
    //
    // The two lists are separate because Ensure has replace semantics: a
    // scope on the app registration that is not in `required` is treated as
    // stale and pruned. A declaration that was merely malformed must not be
    // read that way, so Ensure switches stale cleanup off entirely for any
    // resource named in `unprovisionable` and leaves that resource's scopes
    // and grants exactly as it found them.
    Task<PermissionFixResult> EnsureAsync(
        string tenantId,
        string appClientId,
        IReadOnlyList<PluginPermissionRequirement> required,
        IReadOnlyList<RejectedRequirement> unprovisionable,
        CancellationToken cancellationToken);
}
