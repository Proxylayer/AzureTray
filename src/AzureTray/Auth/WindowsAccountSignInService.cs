using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.AzureCloud;
using AzureTray.Configuration;

namespace AzureTray.Auth;

// Resolves the user's tenant with zero UI when possible. No token is acquired
// here — that happens later when CredentialFactory builds a per-tenant
// credential and the broker reuses the user's existing Windows session.
//
//   1. Read the signed-in Windows user's UPN from the OS (GetUserNameEx).
//   2. Use the UPN's domain to call /.well-known/openid-configuration.
//   3. Pull the tenant ID out of the discovery document's issuer.
//
// On an Entra-joined / AD-joined PC steps 1–3 complete in milliseconds with
// no prompt. If any step fails (non-joined PC, no UPN, OIDC 404), we fall
// back to the WAM broker picker so the user can still add a tenant.
public interface IWindowsAccountSignInService
{
    // Tries the zero-UI auto-detect path first, then falls back to the
    // broker picker. Best when the user wants AzureTray to bind to the
    // tenant their Windows session is already signed into.
    Task<WindowsAccount> SignInAsync(CancellationToken cancellationToken);

    // Always opens the WAM broker picker. The picker lets the user enter
    // an email (LoginHint pre-fills it when supplied) OR pick from any
    // work/school accounts they've added via Windows Settings → Accounts →
    // Email & accounts → "Add a work or school account". Use this when the
    // tenant they want isn't the active Windows session.
    Task<WindowsAccount> SignInWithPickerAsync(string? loginHint, CancellationToken cancellationToken);
}

// Email is the best value to use as a sign-in LoginHint. For a B2B guest it is
// the clean home email derived from the token's email/preferred_username
// claims, whereas UserPrincipalName may be the synthetic "#EXT#" form. Null
// when no clean address could be resolved (see SignInHint).
public sealed record WindowsAccount(string TenantId, string DisplayName, string UserPrincipalName, string Domain, string? Email = null);

public sealed class WindowsAccountSignInService : IWindowsAccountSignInService
{
    private readonly AuthOptions _auth;
    private readonly IAzureCloudConfig _cloud;
    private readonly IOpenIdConfigClient _oidc;
    private readonly ILogger<WindowsAccountSignInService> _logger;

    public WindowsAccountSignInService(
        IOptions<AuthOptions> auth,
        IAzureCloudConfig cloud,
        IOpenIdConfigClient oidc,
        ILogger<WindowsAccountSignInService> logger)
    {
        _auth = auth.Value;
        _cloud = cloud;
        _oidc = oidc;
        _logger = logger;
    }

    public async Task<WindowsAccount> SignInAsync(CancellationToken cancellationToken)
    {
        // 1. Zero-UI path: read the Windows-signed-in UPN, do OIDC, return.
        //    No broker call here — adding one forces an interactive sign-in
        //    when MSAL can't satisfy silent SSO against the public client,
        //    which defeats the "use the Windows session" intent.
        var windowsUpn = TryGetWindowsUpn();
        if (!string.IsNullOrWhiteSpace(windowsUpn))
        {
            var domain = ExtractDomain(windowsUpn!);
            if (!string.IsNullOrWhiteSpace(domain))
            {
                try
                {
                    var discovery = await _oidc.DiscoverAsync(domain, cancellationToken).ConfigureAwait(false);
                    if (discovery is not null && !string.IsNullOrWhiteSpace(discovery.TenantId))
                    {
                        _logger.LogInformation(
                            "Auto-detected tenant {TenantId} from Windows UPN {Upn} via OIDC discovery — no prompt shown.",
                            discovery.TenantId, windowsUpn);
                        return new WindowsAccount(
                            TenantId: discovery.TenantId,
                            DisplayName: windowsUpn!,
                            UserPrincipalName: windowsUpn!,
                            Domain: domain,
                            // Zero-UI path targets the user's own Windows
                            // session, never a guest — the Windows UPN is
                            // already the clean home email.
                            Email: windowsUpn);
                    }
                    _logger.LogDebug("OIDC discovery returned no tenant for {Domain}; falling back to broker picker.", domain);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OIDC auto-detect for {Domain} failed; falling back to broker picker.", domain);
                }
            }
        }

        // 2. Fallback: WAM broker picker. Used when the PC isn't joined, when
        //    OIDC discovery fails, or when the user wants to add a different
        //    tenant than their Windows identity.
        return await SignInWithBrokerPickerAsync(loginHint: null, cancellationToken).ConfigureAwait(false);
    }

    public Task<WindowsAccount> SignInWithPickerAsync(string? loginHint, CancellationToken cancellationToken)
        => SignInWithBrokerPickerAsync(loginHint, cancellationToken);

    private async Task<WindowsAccount> SignInWithBrokerPickerAsync(string? loginHint, CancellationToken cancellationToken)
    {
        var configuredClientId = !string.IsNullOrWhiteSpace(_auth.ClientId) ? _auth.ClientId : null;
        var clientId = configuredClientId ?? AuthDefaults.PublicClientId;

        // WAM broker requires ms-appx-web://microsoft.aad.brokerplugin/{clientId}
        // registered on the app. The Azure CLI fallback doesn't have that URI, so
        // only use the WAM broker path when a dedicated client ID is configured.
        InteractiveBrowserCredential credential;
        if (configuredClientId is not null)
        {
            var brokerOptions = new InteractiveBrowserCredentialBrokerOptions(GetParentWindowHandle())
            {
                TenantId = "organizations",
                ClientId = clientId,
                UseDefaultBrokerAccount = false,
                LoginHint = string.IsNullOrWhiteSpace(loginHint) ? null : loginHint,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "AzureTray-broker-bootstrap",
                },
            };
            credential = new InteractiveBrowserCredential(brokerOptions);
        }
        else
        {
            _logger.LogDebug(
                "No ClientId configured; using browser-based sign-in (WAM broker requires a registered redirect URI).");
            var browserOptions = new InteractiveBrowserCredentialOptions
            {
                TenantId = "organizations",
                ClientId = clientId,
                LoginHint = string.IsNullOrWhiteSpace(loginHint) ? null : loginHint,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = "AzureTray-broker-bootstrap",
                },
            };
            credential = new InteractiveBrowserCredential(browserOptions);
        }
        var token = await credential.GetTokenAsync(
            new TokenRequestContext(new[] { _cloud.GraphScope }),
            cancellationToken).ConfigureAwait(false);

        var claims = DecodeJwtClaims(token.Token);
        if (!claims.TryGetValue("tid", out var tenantId) || string.IsNullOrWhiteSpace(tenantId))
        {
            throw new InvalidOperationException(
                "Token returned from the broker has no tenant (tid) claim; cannot resolve the tenant.");
        }

        var upn = claims.GetValueOrDefault("upn")
            ?? claims.GetValueOrDefault("preferred_username")
            ?? string.Empty;
        var displayName = claims.GetValueOrDefault("name")
            ?? (string.IsNullOrEmpty(upn) ? tenantId : upn);

        // Resolve the clean sign-in email. For a guest, `upn` is the synthetic
        // "#EXT#" form (no password of its own); the `email` /
        // `preferred_username` claims usually carry the real home address, so
        // prefer those and only fall back to un-mangling the #EXT# UPN.
        var email = SignInHint.Pick(
            claims.GetValueOrDefault("email"),
            claims.GetValueOrDefault("preferred_username"),
            upn);

        // Anchor Domain on the clean email when we have one so OIDC discovery
        // targets the user's real home domain rather than the resource tenant.
        var domain = ExtractDomain(email ?? upn);

        return new WindowsAccount(tenantId, displayName, upn, domain, email);
    }

    // GetUserNameEx with NameUserPrincipal returns the signed-in user's UPN
    // on Entra-joined / AD-joined machines. Returns false on a workgroup PC
    // or local account — that's our signal to fall back to the broker picker.
    private const int NameUserPrincipal = 8;

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetUserNameEx(int nameFormat, [Out] char[] userName, ref uint size);

    private static string? TryGetWindowsUpn()
    {
        try
        {
            uint size = 1024;
            var buffer = new char[size];
            if (GetUserNameEx(NameUserPrincipal, buffer, ref size))
            {
                var upn = new string(buffer, 0, (int)size);
                return string.IsNullOrWhiteSpace(upn) ? null : upn;
            }
        }
        catch
        {
            // Any failure is fine — the broker fallback covers it.
        }
        return null;
    }

    private static string ExtractDomain(string upn)
    {
        if (string.IsNullOrEmpty(upn)) return string.Empty;
        var at = upn.IndexOf('@');
        return at >= 0 && at < upn.Length - 1 ? upn[(at + 1)..] : string.Empty;
    }

    // Minimal JWT decoder for payload claims. The broker already validated
    // the signature; we only need the well-known fields (tid, name, upn).
    private static Dictionary<string, string> DecodeJwtClaims(string jwt)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var parts = jwt.Split('.');
        if (parts.Length < 2) return result;

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        switch (payload.Length % 4)
        {
            case 2: payload += "=="; break;
            case 3: payload += "="; break;
        }

        byte[] bytes;
        try { bytes = Convert.FromBase64String(payload); }
        catch (FormatException) { return result; }

        var json = Encoding.UTF8.GetString(bytes);
        using var doc = JsonDocument.Parse(json);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            if (prop.Value.ValueKind == JsonValueKind.String)
            {
                result[prop.Name] = prop.Value.GetString()!;
            }
        }
        return result;
    }

    private static IntPtr GetParentWindowHandle()
    {
        try
        {
            var fg = GetForegroundWindow();
            return fg == IntPtr.Zero ? Process.GetCurrentProcess().MainWindowHandle : fg;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
