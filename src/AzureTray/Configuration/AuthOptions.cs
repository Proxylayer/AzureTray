namespace AzureTray.Configuration;

public sealed class AuthOptions
{
    public const string SectionName = "App:Auth";

    public string ClientId { get; init; } = string.Empty;

    public string RedirectUri { get; init; } = "http://localhost";

    public int TokenAcquisitionTimeoutSeconds { get; init; } = 30;

    // How often the background token monitor silently re-probes each ready
    // tenant to detect a refresh token that expired mid-session. Clamped to a
    // 30s floor at runtime. Set lower in development to exercise the re-auth
    // popup / Settings button without waiting for a real expiry.
    public int TokenMonitorIntervalSeconds { get; init; } = 300;

    // Display-name (or prefix) of the app registration the host tries to
    // auto-discover when adding a tenant via "Sign in with Windows" or a
    // domain lookup. After the broker / OIDC step resolves the tenant ID,
    // the host queries Graph applications by this name; an exact-name match
    // (or a single prefix match) is stored as that tenant's per-tenant
    // ClientId. Leave empty to skip auto-discovery and always fall back to
    // the global ClientId.
    public string AppRegistrationName { get; init; } = "AzureTray";
}
