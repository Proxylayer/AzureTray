using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using AzureTray.AzureCloud;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Auth;

// Why the caller's refresh had to become a wait-and-verify:
//
// Admin consent lands in Entra tens of seconds before the STS will mint a
// token carrying it. Refreshing immediately after the consent write burns the
// one refresh at the exact moment it is guaranteed to be too early, and MSAL
// then caches that still-stale token for its full lifetime — so every call
// needing the new scope keeps returning 403 PermissionScopeNotGranted for the
// next hour, naming the scope that was just granted.
//
// So this refreshes on a short backoff and checks the token's own "scp" claim
// each time, stopping as soon as the new scopes actually appear. It reports;
// it never throws — a permission fix that succeeded must not be turned into a
// reported failure by the verification that follows it.
internal sealed class ConsentPropagationRefresher
{
    // Four tries spread over ~50s. Long enough to cover the usual propagation
    // delay, short enough that a user watching the status line gets an answer
    // rather than a spinner.
    private static readonly IReadOnlyList<TimeSpan> DefaultAttemptDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(20),
    ];

    private readonly ICredentialFactory _credentials;
    private readonly IAzureCloudConfig _cloud;
    private readonly ILogger _logger;
    private readonly IReadOnlyList<TimeSpan> _attemptDelays;

    public ConsentPropagationRefresher(
        ICredentialFactory credentials,
        IAzureCloudConfig cloud,
        ILogger logger,
        IReadOnlyList<TimeSpan>? attemptDelays = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(logger);

        _credentials = credentials;
        _cloud = cloud;
        _logger = logger;
        _attemptDelays = attemptDelays is { Count: > 0 } ? attemptDelays : DefaultAttemptDelays;
    }

    // Refreshes the tenant's access tokens and — when newlyGranted is
    // non-empty — keeps refreshing on a backoff until the tokens carry those
    // scopes or the attempts run out.
    //
    // An empty newlyGranted is the "nothing changed, refresh anyway" case:
    // one pass over both resources with nothing to verify against.
    public async Task<ConsentRefreshOutcome> RefreshAsync(
        string tenantId,
        IReadOnlyList<PluginPermissionRequirement> newlyGranted,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(newlyGranted);

        try
        {
            var expected = GroupByResourceScope(newlyGranted);
            return expected.Count == 0
                ? await RefreshWithoutVerifyingAsync(tenantId, cancellationToken).ConfigureAwait(false)
                : await RefreshUntilGrantedAsync(tenantId, expected, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ConsentRefreshOutcome(
                ConsentRefreshState.Unverified,
                " Token refresh was cancelled — sign out and back in if calls keep failing.");
        }
        catch (Exception ex)
        {
            // Belt and braces: RefreshAndReadScopesAsync already swallows the
            // expected failures, and nothing here may escalate into "the
            // permission fix failed".
            _logger.LogWarning(ex,
                "Post-consent token refresh for tenant {TenantId} failed; the permission changes themselves are unaffected.",
                tenantId);
            return new ConsentRefreshOutcome(
                ConsentRefreshState.Unverified,
                " Permissions applied, but the token refresh failed — sign out and back in to pick up the new scopes.");
        }
    }

    private async Task<ConsentRefreshOutcome> RefreshWithoutVerifyingAsync(
        string tenantId, CancellationToken cancellationToken)
    {
        var acquired = false;
        foreach (var resourceScope in new[] { _cloud.GraphScope, _cloud.ArmScope })
        {
            var granted = await _credentials
                .RefreshAndReadScopesAsync(tenantId, resourceScope, cancellationToken)
                .ConfigureAwait(false);
            if (granted is not null) acquired = true;
        }

        _logger.LogInformation(
            "Unconditional token refresh for tenant {TenantId}: acquired={Acquired}.",
            tenantId, acquired);

        return acquired
            ? new ConsentRefreshOutcome(
                ConsentRefreshState.Confirmed,
                " Access tokens refreshed — any consent granted since the last sign-in is in effect now.")
            : new ConsentRefreshOutcome(
                ConsentRefreshState.SignInRequired,
                " Tokens could not be refreshed silently — sign out and back in.");
    }

    private async Task<ConsentRefreshOutcome> RefreshUntilGrantedAsync(
        string tenantId,
        Dictionary<string, HashSet<string>> expected,
        CancellationToken cancellationToken)
    {
        var waited = TimeSpan.Zero;
        var unverifiable = false;

        for (var attempt = 0; attempt < _attemptDelays.Count && expected.Count > 0; attempt++)
        {
            // The first attempt waits too: the consent write has only just
            // returned, and asking the STS straight away is the one timing
            // guaranteed to produce a stale token.
            await Task.Delay(_attemptDelays[attempt], cancellationToken).ConfigureAwait(false);
            waited += _attemptDelays[attempt];

            foreach (var resourceScope in expected.Keys.ToList())
            {
                var refreshed = await _credentials
                    .RefreshAndReadScopesAsync(tenantId, resourceScope, cancellationToken)
                    .ConfigureAwait(false);

                if (refreshed is null)
                {
                    _logger.LogInformation(
                        "Post-consent refresh for tenant {TenantId} resource {Resource}: no token could be acquired silently.",
                        tenantId, resourceScope);
                    return new ConsentRefreshOutcome(
                        ConsentRefreshState.SignInRequired,
                        " Permissions applied, but the tokens could not be refreshed silently — sign out and back in to pick up the new scopes.");
                }

                // Judge on the token the rest of the app will be served, not
                // on the one the STS just handed us: the whole defect is that
                // the *cached* token outlives the consent change. This read
                // comes out of the MSAL cache the refresh above just wrote,
                // so it costs no round-trip.
                var granted = await ReadCachedScopesAsync(tenantId, resourceScope, cancellationToken)
                    .ConfigureAwait(false) ?? refreshed;

                if (granted.Count == 0)
                {
                    // Token acquired but its scopes are unreadable (opaque or
                    // encrypted token format). Retrying cannot change that.
                    unverifiable = true;
                    expected.Remove(resourceScope);
                    continue;
                }

                var stillMissing = expected[resourceScope]
                    .Where(name => !granted.Contains(name, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (stillMissing.Count == 0)
                {
                    expected.Remove(resourceScope);
                    continue;
                }

                expected[resourceScope] = new HashSet<string>(stillMissing, StringComparer.OrdinalIgnoreCase);
                _logger.LogInformation(
                    "Post-consent refresh for tenant {TenantId} resource {Resource}, attempt {Attempt} after {Waited:F0}s: still missing {Missing}.",
                    tenantId, resourceScope, attempt + 1, waited.TotalSeconds, string.Join(", ", stillMissing));
            }
        }

        if (expected.Count == 0 && !unverifiable)
        {
            _logger.LogInformation(
                "Post-consent refresh for tenant {TenantId} converged after {Waited:F0}s; the new scopes are present in the tokens.",
                tenantId, waited.TotalSeconds);
            return new ConsentRefreshOutcome(
                ConsentRefreshState.Confirmed,
                " Access tokens refreshed and confirmed to carry the new scopes.");
        }

        if (expected.Count == 0)
        {
            return new ConsentRefreshOutcome(
                ConsentRefreshState.Unverified,
                " Access tokens refreshed, but the new scopes could not be read back from them — sign out and back in if calls keep failing.");
        }

        var missing = string.Join(", ", expected.Values.SelectMany(names => names).Distinct(StringComparer.OrdinalIgnoreCase));
        _logger.LogWarning(
            "Post-consent refresh for tenant {TenantId} gave up after {Waited:F0}s; tokens still lack {Missing}. Entra consent propagation can outlast the wait.",
            tenantId, waited.TotalSeconds, missing);
        return new ConsentRefreshOutcome(
            ConsentRefreshState.NotPropagated,
            $" Permissions applied, but after {waited.TotalSeconds:F0}s the refreshed tokens still lack {missing} — Entra has not propagated the consent yet. Sign out and back in (or retry Refresh tokens) in a minute.");
    }

    // The scopes on whatever token a silent acquire serves right now — i.e.
    // what every other caller in the app will get. Null when the token cannot
    // be read at all, which the caller treats as "cannot tell".
    private async Task<IReadOnlyList<string>?> ReadCachedScopesAsync(
        string tenantId, string resourceScope, CancellationToken cancellationToken)
    {
        try
        {
            var token = await _credentials.GetForTenant(tenantId)
                .GetTokenAsync(new TokenRequestContext([resourceScope]), cancellationToken)
                .ConfigureAwait(false);
            return JwtAccessTokenScopes.TryRead(token.Token);
        }
        catch (AuthenticationRequiredException)
        {
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Could not read the cached token for tenant {TenantId} resource {Resource}; falling back to the refreshed token's scopes.",
                tenantId, resourceScope);
            return null;
        }
    }

    // Keyed by the resource-wide ".default" scope the tokens are acquired
    // with, because that is the granularity a token is issued at. Unknown
    // PermissionApi values are dropped: they name no resource we hold tokens
    // for, so there is nothing to verify.
    private Dictionary<string, HashSet<string>> GroupByResourceScope(
        IReadOnlyList<PluginPermissionRequirement> requirements)
    {
        var byResource = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var requirement in requirements)
        {
            if (requirement is null || string.IsNullOrWhiteSpace(requirement.ScopeName)) continue;

            var resourceScope = requirement.Api switch
            {
                PermissionApi.MicrosoftGraph => _cloud.GraphScope,
                PermissionApi.AzureResourceManager => _cloud.ArmScope,
                _ => null,
            };
            if (resourceScope is null) continue;

            if (!byResource.TryGetValue(resourceScope, out var names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                byResource[resourceScope] = names;
            }
            names.Add(requirement.ScopeName);
        }
        return byResource;
    }
}

internal enum ConsentRefreshState
{
    // Tokens were refreshed and (where there was something to check) carry
    // the expected scopes.
    Confirmed,

    // Tokens were refreshed but what they grant could not be read back.
    Unverified,

    // The STS is still issuing tokens without the new scopes.
    NotPropagated,

    // No token could be acquired silently; the user has to sign in again.
    SignInRequired,
}

// StatusSuffix is appended to the tenant action status line, so it starts
// with a space and reads as a continuation of the sentence before it.
internal sealed record ConsentRefreshOutcome(ConsentRefreshState State, string StatusSuffix);
