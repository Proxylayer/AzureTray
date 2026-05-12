using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

// The minimum delegated permissions the host itself needs, independent
// of any loaded plugin. Currently just User.Read so the host can call
// /me to resolve the signed-in user's tenant/display name. Plugins add
// their own scopes via ITrayPlugin.RequiredPermissions and are
// aggregated alongside these in Fix Permissions / Create App Registration.
internal static class HostRequiredPermissions
{
    public static IReadOnlyList<PluginPermissionRequirement> All { get; } = new[]
    {
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "User.Read",
            "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
            "Sign in and read user profile (host /me lookup)"),
    };
}
