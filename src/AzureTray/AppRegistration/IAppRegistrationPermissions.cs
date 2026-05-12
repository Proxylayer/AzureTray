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

    Task<PermissionFixResult> EnsureAsync(
        string tenantId,
        string appClientId,
        IReadOnlyList<PluginPermissionRequirement> required,
        CancellationToken cancellationToken);
}
