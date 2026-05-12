namespace AzureTray.Models;

// In-app domain model. ClientId is optional: when null/empty, the credential
// factory falls back to App:Auth:ClientId from configuration.
//
// SignInEmail is the UPN that was originally used to add this tenant (from
// the Windows-account sign-in's Graph /me, or from the manual Save's /me
// lookup). When set, it is passed as MSAL's LoginHint so the broker
// pre-fills the sign-in dialog with the right account — the user doesn't
// have to remember which UPN they're supposed to use for this tenant.
//
// ProbeDisabled silences the startup readiness probe for this tenant. The
// "Disable" button on the sign-in-required notification flips it true; any
// successful interactive sign-in flips it back to false. The tenant stays
// in the list, just stops auto-prompting on launch.
public sealed record Tenant(
    string TenantId,
    string DisplayName,
    string? ClientId,
    string? SignInEmail = null,
    bool ProbeDisabled = false);
