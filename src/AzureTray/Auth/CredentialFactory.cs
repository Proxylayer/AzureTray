using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using Azure.Core;
using Azure.Identity;
using Azure.Identity.Broker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.Configuration;
using AzureTray.Tenants;

namespace AzureTray.Auth;

public sealed class CredentialFactory : ICredentialFactory
{
    private static readonly string[] GraphDefaultScopes = ["https://graph.microsoft.com/.default"];

    private readonly AuthOptions _options;
    private readonly ITenantStore _tenantStore;
    private readonly IAppPaths _paths;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<CredentialFactory> _logger;
    private readonly ConcurrentDictionary<string, TokenCredential> _byTenant
        = new(StringComparer.OrdinalIgnoreCase);

    // Per-tenant AuthenticationRecord. SignInAsync writes one; BuildForTenant
    // reads it (loading from disk on first miss) and plumbs it into the
    // silent credential's options so MSAL knows which cached account to
    // look up tokens for. Persisting these to disk lets the startup probe
    // use the previously-cached token without re-prompting the user — the
    // refresh token in MSAL's encrypted cache + the record pointing at the
    // account is enough for a silent token acquire on app restart.
    private readonly ConcurrentDictionary<string, AuthenticationRecord> _authRecords
        = new(StringComparer.OrdinalIgnoreCase);

    // ForceRefreshAsync serializes per tenant and ignores repeat calls that
    // arrive right behind a successful one — several approvals landing in the
    // same poll shouldn't mean several STS round-trips.
    private static readonly TimeSpan ForceRefreshCooldown = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastForcedRefresh
        = new(StringComparer.OrdinalIgnoreCase);

    public CredentialFactory(
        IOptions<AuthOptions> options,
        ITenantStore tenantStore,
        IAppPaths paths,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _tenantStore = tenantStore;
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<CredentialFactory>();
    }

    public TokenCredential GetForTenant(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        return _byTenant.GetOrAdd(tenantId, id => BuildForTenant(id, disableAutomaticAuth: true));
    }

    public void Invalidate(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        if (_byTenant.TryRemove(tenantId, out var credential) && credential is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    public async Task<bool> ForceRefreshAsync(
        string tenantId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(scopes);

        var targets = scopes
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (targets.Length == 0) return false;

        // One refresh at a time per tenant. Several plugins (or several
        // approvals landing together) can ask concurrently; the gate plus the
        // cooldown collapse that into a single round-trip to the STS.
        var gate = _refreshGates.GetOrAdd(tenantId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastForcedRefresh.TryGetValue(tenantId, out var last)
                && DateTimeOffset.UtcNow - last < ForceRefreshCooldown)
            {
                _logger.LogDebug(
                    "Token force-refresh for tenant {TenantId} skipped: one completed {SecondsAgo:F0}s ago.",
                    tenantId, (DateTimeOffset.UtcNow - last).TotalSeconds);
                return true;
            }

            var refreshedAny = false;
            foreach (var scope in targets)
            {
                refreshedAny |= await ForceRefreshScopeAsync(tenantId, scope, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (refreshedAny) _lastForcedRefresh[tenantId] = DateTimeOffset.UtcNow;
            return refreshedAny;
        }
        finally
        {
            gate.Release();
        }
    }

    // Acquires one scope's token twice: once normally (which serves whatever
    // MSAL has cached) and once with a claims challenge, which MSAL treats as
    // "skip the access-token cache" and satisfies from the refresh token — so
    // the STS re-issues the token with the account's current claims. The two
    // tokens are compared by hash, never logged, so the caller can tell a real
    // refresh from a cache hit.
    private async Task<bool> ForceRefreshScopeAsync(string tenantId, string scope, CancellationToken ct)
    {
        string[] scopeArray = [scope];

        var credential = GetForTenant(tenantId);
        string beforeHash;
        try
        {
            var before = await credential.GetTokenAsync(new TokenRequestContext(scopeArray), ct)
                .ConfigureAwait(false);
            beforeHash = HashToken(before.Token);
        }
        catch (AuthenticationRequiredException)
        {
            _logger.LogInformation(
                "Token force-refresh for tenant {TenantId} skipped: no silently usable token for {Scope} (re-auth needed).",
                tenantId, scope);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Token force-refresh for tenant {TenantId} could not read the current token for {Scope}.",
                tenantId, scope);
            return false;
        }

        // First attempt: claims challenge on the existing credential.
        if (await TryAcquireBypassingCacheAsync(credential, tenantId, scopeArray, beforeHash, ct).ConfigureAwait(false))
        {
            return true;
        }

        // Second attempt: rebuild the credential (fresh MSAL client over the
        // same persisted cache) and challenge again. This covers the case where
        // the in-memory client, not the persisted cache, is what pinned the
        // stale token.
        Rebuild(tenantId);
        if (await TryAcquireBypassingCacheAsync(GetForTenant(tenantId), tenantId, scopeArray, beforeHash, ct).ConfigureAwait(false))
        {
            return true;
        }

        _logger.LogWarning(
            "Token force-refresh for tenant {TenantId} scope {Scope} returned the same token; new claims will only appear when the cached token rolls over.",
            tenantId, scope);
        return false;
    }

    private async Task<bool> TryAcquireBypassingCacheAsync(
        TokenCredential credential,
        string tenantId,
        string[] scopeArray,
        string beforeHash,
        CancellationToken ct)
    {
        try
        {
            var context = new TokenRequestContext(
                scopeArray,
                parentRequestId: null,
                claims: BuildCacheBypassClaims());
            var after = await credential.GetTokenAsync(context, ct).ConfigureAwait(false);

            if (string.Equals(HashToken(after.Token), beforeHash, StringComparison.Ordinal))
            {
                return false;
            }

            _logger.LogInformation(
                "Token force-refreshed for tenant {TenantId} scope {Scope}; new token expires {ExpiresOn:u}.",
                tenantId, scopeArray[0], after.ExpiresOn);
            return true;
        }
        catch (AuthenticationRequiredException)
        {
            // DisableAutomaticAuthentication is intentional — the silent path
            // could not satisfy the challenge, and we will not pop a broker
            // window from a background approval poll.
            _logger.LogInformation(
                "Token force-refresh for tenant {TenantId} scope {Scope} needs interactive sign-in; leaving the cached token in place.",
                tenantId, scopeArray[0]);
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Token force-refresh for tenant {TenantId} scope {Scope} failed.",
                tenantId, scopeArray[0]);
            return false;
        }
    }

    // A minimal CAE-style claims challenge: "issue a token valid no earlier
    // than now". MSAL skips its access-token cache whenever claims are present,
    // and the STS honours nbf by minting a fresh token off the refresh token —
    // no user interaction involved.
    private static string BuildCacheBypassClaims()
    {
        var notBefore = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{{\"access_token\":{{\"nbf\":{{\"essential\":true,\"value\":\"{notBefore}\"}}}}}}";
    }

    // Compared, never logged: the hash is only used to answer "did the STS
    // hand back a different token than the cache held?".
    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    // Swaps in a freshly built credential without disposing the outgoing one:
    // a concurrent caller may still be inside its GetTokenAsync, and disposing
    // its gate underneath would throw ObjectDisposedException. SemaphoreSlim
    // holds no unmanaged handle here, so letting the GC collect it is safe.
    private void Rebuild(string tenantId)
        => _byTenant[tenantId] = BuildForTenant(tenantId, disableAutomaticAuth: true);

    public async Task SignInAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // WAM (the Windows broker) requires the parent HWND and the
        // MSAL call to come from a UI/STA thread with a pumped message
        // loop. The probe / notification's continuation runs on the
        // thread-pool, so we marshal the entire interactive call onto
        // the WPF dispatcher — including HWND resolution, since the
        // foreground/main-window HWND must be valid at the moment WAM
        // anchors to it (the notification window we were just showing
        // is already closed by the time we get here).
        var record = await RunOnUiThreadAsync(async () =>
        {
            var parentHwnd = ResolveParentHwndOnUiThread();
            var clientId = ResolveClientId(tenantId);
            var loginHint = TryGetLoginHint(tenantId);
            var options = new InteractiveBrowserCredentialBrokerOptions(parentHwnd)
            {
                TenantId = tenantId,
                ClientId = clientId,
                // Interactive mode: don't auto-use the Windows account
                // silently — show the real broker picker so the user
                // can choose / sign in with the right account.
                UseDefaultBrokerAccount = false,
                LoginHint = loginHint,
                TokenCachePersistenceOptions = new TokenCachePersistenceOptions
                {
                    Name = $"AzureTray-{tenantId}",
                },
            };

            var interactive = new InteractiveBrowserCredential(options);
            var ctx = new TokenRequestContext(GraphDefaultScopes);
            // AuthenticateAsync is the explicit interactive entry-point —
            // unlike GetTokenAsync, it always drives WAM rather than running
            // a "silent first" path that can throw AuthRequired before the
            // UI gets a chance to appear.
            return await interactive.AuthenticateAsync(ctx, cancellationToken).ConfigureAwait(true);
        }).ConfigureAwait(false);

        _authRecords[tenantId] = record;
        await PersistAuthRecordAsync(tenantId, record, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation(
            "Interactive sign-in completed for tenant {TenantId} as {Username}.",
            tenantId, record.Username);

        // Drop the cached silent credential so the next caller rebuilds it
        // with the freshly-stored AuthenticationRecord.
        Invalidate(tenantId);
    }

    // Marshals an async operation onto the WPF dispatcher. Returns the
    // result back to the calling context. If the WPF Application isn't
    // running (e.g. shutdown), runs inline — the caller's exception
    // handler will surface whatever happens.
    private static async Task<T> RunOnUiThreadAsync<T>(Func<Task<T>> work)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            return await work().ConfigureAwait(false);
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { tcs.TrySetResult(await work().ConfigureAwait(true)); }
            catch (OperationCanceledException oce) { tcs.TrySetCanceled(oce.CancellationToken); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task.ConfigureAwait(false);
    }

    // Picks a stable parent HWND for the broker. Must be called from the
    // UI thread because Application.Current.Windows is dispatcher-affined
    // and the chosen window's handle must be alive at the moment WAM
    // anchors to it.
    private static IntPtr ResolveParentHwndOnUiThread()
    {
        // Prefer a visible, top-level WPF window — that's the user's
        // current app context and gives WAM a real target for modality.
        if (System.Windows.Application.Current?.Windows is { } windows)
        {
            foreach (System.Windows.Window window in windows)
            {
                if (!window.IsVisible) continue;
                var handle = new WindowInteropHelper(window).Handle;
                if (handle != IntPtr.Zero) return handle;
            }
            // Fall back to MainWindow even if Visible flag is false yet.
            if (System.Windows.Application.Current?.MainWindow is { } main)
            {
                var handle = new WindowInteropHelper(main).Handle;
                if (handle != IntPtr.Zero) return handle;
            }
        }

        // Last-resort: the OS-level main window of this process, or the
        // current foreground. WAM accepts IntPtr.Zero too, but giving it
        // a real HWND when available improves dialog placement.
        try
        {
            var fg = GetForegroundWindow();
            if (fg != IntPtr.Zero) return fg;
        }
        catch { /* ignore — fall through */ }
        return Process.GetCurrentProcess().MainWindowHandle;
    }

    // Loads an AuthenticationRecord from in-memory cache, falling back to
    // disk on first miss. A null result is fine — BuildForTenant simply
    // builds the credential without a record, and the silent attempt will
    // either find the right account via LoginHint or throw AuthRequired
    // (and we route to the sign-in notification from there).
    private AuthenticationRecord? TryGetAuthRecord(string tenantId)
    {
        if (_authRecords.TryGetValue(tenantId, out var cached)) return cached;

        var path = AuthRecordPath(tenantId);
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = File.OpenRead(path);
            var record = AuthenticationRecord.Deserialize(stream);
            _authRecords[tenantId] = record;
            return record;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to deserialize cached AuthenticationRecord for tenant {TenantId}; the user will be prompted to re-sign-in.",
                tenantId);
            return null;
        }
    }

    private async Task PersistAuthRecordAsync(string tenantId, AuthenticationRecord record, CancellationToken cancellationToken)
    {
        try
        {
            var dir = Path.GetDirectoryName(AuthRecordPath(tenantId));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            await using var stream = File.Create(AuthRecordPath(tenantId));
            await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Persistence is best-effort: even if it fails, the in-memory
            // record covers the current session.
            _logger.LogWarning(ex,
                "Failed to persist AuthenticationRecord for tenant {TenantId}; sign-in won't survive an app restart but this session is fine.",
                tenantId);
        }
    }

    private string AuthRecordPath(string tenantId)
        => Path.Combine(_paths.DataDir, "auth-records", $"{tenantId}.bin");

    private SerializedTokenCredential BuildForTenant(string tenantId, bool disableAutomaticAuth)
    {
        // ResolveClientId always returns a non-empty value — the public
        // Azure CLI client id is the bottom of the fallback chain — so the
        // app is fully usable without any appsettings.json config.
        var clientId = ResolveClientId(tenantId);

        // Hand MSAL the Web Account Manager broker. Two distinct modes:
        //
        // disableAutomaticAuth = true  (silent background credential):
        //   UseDefaultBrokerAccount = true so the first token request tries
        //   the signed-in Windows account silently — on an Entra-joined PC
        //   there is no browser, no prompt. Any need for interaction throws
        //   AuthenticationRequiredException instead of popping a window;
        //   the caller (probe) surfaces a notification asking the user to
        //   explicitly initiate sign-in.
        //
        // The AuthenticationRecord captured by SignInAsync is plumbed in here
        // so the silent credential can locate cached tokens for the right
        // account — without it, MSAL's silent lookup against an empty
        // in-memory account list fails even when the persisted cache has
        // the token.
        var loginHint = TryGetLoginHint(tenantId);
        var record = TryGetAuthRecord(tenantId);
        var options = new InteractiveBrowserCredentialBrokerOptions(GetParentWindowHandle())
        {
            TenantId = tenantId,
            ClientId = clientId,
            UseDefaultBrokerAccount = disableAutomaticAuth,
            DisableAutomaticAuthentication = disableAutomaticAuth,
            LoginHint = loginHint,
            AuthenticationRecord = record,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions
            {
                Name = $"AzureTray-{tenantId}",
            },
        };

        var inner = new InteractiveBrowserCredential(options);

        var timeout = TimeSpan.FromSeconds(_options.TokenAcquisitionTimeoutSeconds);
        return new SerializedTokenCredential(
            inner,
            timeout,
            label: tenantId,
            logger: _loggerFactory.CreateLogger<SerializedTokenCredential>());
    }

    // Returns the stored UPN for the tenant, or null if none recorded.
    // Used as MSAL's LoginHint so the broker pre-fills the sign-in dialog
    // with the account the user originally used to add this tenant.
    private string? TryGetLoginHint(string tenantId)
    {
        var stored = _tenantStore.FindByTenantId(tenantId);
        return string.IsNullOrWhiteSpace(stored?.SignInEmail) ? null : stored.SignInEmail;
    }

    // The broker needs a parent HWND to anchor any UI it has to show. The
    // foreground window is a sensible default when sign-in is triggered from
    // a tray menu click or settings dialog. IntPtr.Zero is accepted and means
    // "no parent" — the broker creates its own top-level window.
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

    // Fallback chain: per-tenant override → global App:Auth:ClientId → the
    // Azure CLI public client. The final fallback guarantees we always have
    // something to hand to MSAL, so the host runs zero-config.
    private string ResolveClientId(string tenantId)
    {
        var stored = _tenantStore.FindByTenantId(tenantId);
        if (stored is not null && !string.IsNullOrWhiteSpace(stored.ClientId))
        {
            return stored.ClientId;
        }
        return !string.IsNullOrWhiteSpace(_options.ClientId)
            ? _options.ClientId
            : AuthDefaults.PublicClientId;
    }
}
