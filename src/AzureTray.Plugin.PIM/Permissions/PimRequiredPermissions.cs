using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Permissions;

internal static class PimRequiredPermissions
{
    // Well-known scope IDs for Microsoft Graph and Azure Service Management.
    // These are stable across all Entra tenants.
    //
    // Every id here MUST be the DELEGATED scope id — the matching entry in the
    // resource service principal's oauth2PermissionScopes collection. The host
    // writes it straight into requiredResourceAccess as
    // ResourceAccessDto(id, "Scope"), so an id taken from appRoles (the
    // application-permission list) or copied from a different permission
    // consents the user to something other than what the name says. Verify a
    // new id against the resource SP before adding it, e.g.
    //   az ad sp show --id 00000003-0000-0000-c000-000000000000 \
    //     --query "oauth2PermissionScopes[?value=='<Scope.Name>'].id"
    public static IReadOnlyList<PluginPermissionRequirement> All { get; } = new[]
    {
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "User.Read",
            "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
            "Sign in and read user profile"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "RoleAssignmentSchedule.ReadWrite.Directory",
            "8c026be3-8e26-4774-9372-8d5d6f21daff",
            "Submit self-activation requests for Entra ID roles"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "RoleEligibilitySchedule.Read.Directory",
            "eb0788c2-6d4e-4658-8c9e-c0fb8053f03d",
            "List eligible and currently active Entra ID role assignments"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "PrivilegedAccess.ReadWrite.AzureAD",
            "3c3c74f5-cdaa-4a97-b7e0-4e788bfcfb37",
            "List, fetch, and approve Entra ID PIM approval requests"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "RoleManagement.Read.Directory",
            "741c54c3-0c1e-44a1-818b-3f97ab4e8c83",
            "Read PIM policies and poll activation request status"),
        // PIM for Groups. Separate scopes from the directory-role ones above:
        // the AzureADGroup suffix governs identityGovernance/privilegedAccess/
        // group, and no amount of RoleManagement.*.Directory grants it.
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "PrivilegedEligibilitySchedule.Read.AzureADGroup",
            "8f44f93d-ecef-46ae-a9bf-338508d44d6b",
            "List eligible PIM group memberships and ownerships"),
        // The ReadWrite assignment scope also covers the corresponding reads
        // (active assignments, request status) and the approver-side approve /
        // deny, so the matching .Read scope would be redundant and is not
        // requested. PrivilegedAccess.*.AzureADGroup would work too but grants
        // considerably more than this plugin uses.
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup",
            "06dbc45d-6708-4ef0-a797-f797ee68bf4b",
            "Activate and deactivate group membership, and approve group activation requests"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "RoleManagementPolicy.Read.AzureADGroup",
            "7e26fdff-9cb1-4e56-bede-211fe0e420e8",
            "Read PIM for Groups activation policies (maximum duration, approval requirement)"),
        new PluginPermissionRequirement(
            PermissionApi.AzureResourceManager,
            "user_impersonation",
            "41094075-9dad-400e-a0bd-54e686782033",
            "All Azure RBAC PIM operations on subscriptions and resources"),
    };
}
