namespace AzureTray.Auth;

// Constants for "first-run, no-config" auth so the host works out of the box
// without anyone having to create an app registration first. These are
// public Microsoft-owned client IDs that are already consented in nearly
// every commercial Entra tenant.
internal static class AuthDefaults
{
    // Microsoft Azure CLI public client id. Documented and used at scale;
    // safe to fall back to when no per-tenant or global ClientId is set.
    // On first consent the user sees "Microsoft Azure CLI" — not the
    // host's display name — which is the only visible quirk. To swap in
    // a dedicated branded app, set App:Auth:ClientId in appsettings.json
    // or create an Entra app registration named "AzureTray" (the host
    // auto-discovers it after the first sign-in).
    public const string PublicClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";
}
