using System.Collections.Generic;
using System.Linq;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

// Splits the host's declared scopes into two layers so the consent
// surface stays honest:
//
//   Baseline   — what every signed-in user needs just to operate the
//                tray (sign-in identity, tenant display name resolution).
//                This is the only set used by the runtime host code that
//                a non-admin user actually exercises.
//
//   AdminTools — what the in-app admin features (Fix Permissions,
//                Create App Registration, App Registration search)
//                additionally need to function. A user who never uses
//                those features doesn't need these scopes. We still
//                add them to the app registration at provision time
//                so administrators can self-heal, but users with no
//                directory role to back them up get a clean "you
//                don't have authority" failure instead of a confusing
//                "Insufficient privileges" 403 with no context.
//
// All = Baseline ∪ AdminTools and is what AggregateRequiredPermissions()
// in SettingsViewModel.cs / TestRegistry.cs already consumes, so this
// reshuffle is a pure rename/documentation change at the call sites.
//
// Plugins declare their own scopes via ITrayPlugin.RequiredPermissions
// and are folded into the same aggregate downstream — those are the
// host's only "what does the runtime need" inputs aside from Baseline.
internal static class HostRequiredPermissions
{
    // Always needed. Pure read of the signed-in user; required by /me
    // and by the /organization → /me companyName fallback.
    public static IReadOnlyList<PluginPermissionRequirement> Baseline { get; } = new[]
    {
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "User.Read",
            "e1fe6dd8-ba31-4d61-89e7-88639da4683d",
            "Sign in and read user profile (host /me lookup)"),
    };

    // Only needed for in-app administration of tenant app registrations.
    // The two scopes together let a holder of an appropriate directory
    // role (Application Admin / Cloud Application Admin / Privileged
    // Role Admin / Global Admin) run Fix Permissions and Create App
    // Registration end-to-end. Without these scopes consented, those
    // features return 403 with the underlying Authorization_RequestDenied
    // — surfaced to the user via the response-body capture in
    // AppRegistrationGraphClient.
    public static IReadOnlyList<PluginPermissionRequirement> AdminTools { get; } = new[]
    {
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "Application.ReadWrite.All",
            "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9",
            "Read, search, and update tenant app registrations (in-app Fix Permissions / Create App Registration)"),
        new PluginPermissionRequirement(
            PermissionApi.MicrosoftGraph,
            "DelegatedPermissionGrant.ReadWrite.All",
            "41ce6ca6-6826-4807-84f1-1c82854f7ee5",
            "Grant tenant-wide admin consent for plugin scopes (in-app Fix Permissions / Create App Registration)"),
    };

    // Union used by Create App Registration / Fix Permissions to provision
    // the full host-side scope set on the app reg. Plugins' own
    // requirements are appended downstream — this list is host-only.
    public static IReadOnlyList<PluginPermissionRequirement> All { get; } =
        Baseline.Concat(AdminTools).ToArray();
}
