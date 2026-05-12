using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.PIM.Permissions;

internal static class PimRequiredPermissions
{
    // Well-known scope IDs for Microsoft Graph and Azure Service Management.
    // These are stable across all Entra tenants.
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
            "ed8d2a04-0374-41f1-aefe-da8ac87ccc87",
            "List eligible and currently active Entra ID role assignments"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "PrivilegedAccess.ReadWrite.AzureAD",
            "3c3c74f5-cdaa-4a97-b7e0-4e788bfcfb37",
            "List, fetch, and approve Entra ID PIM approval requests"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "RoleManagement.Read.Directory",
            "483bed4a-2ad3-4361-a73b-c83ccdbdc53c",
            "Read PIM policies and poll activation request status"),
        new PluginPermissionRequirement(
            PermissionApi.AzureResourceManager,
            "user_impersonation",
            "41094075-9dad-400e-a0bd-54e686782033",
            "All Azure RBAC PIM operations on subscriptions and resources"),
    };
}
