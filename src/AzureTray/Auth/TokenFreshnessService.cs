using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AzureTray.AppRegistration;
using AzureTray.AppRegistration.Internal;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Tenants;

namespace AzureTray.Auth;

// Periodic detector for access tokens that have outlived a consent change.
//
// The defect it heals: a token is issued for a resource's ".default" scope,
// so it carries whatever was consented at the moment it was minted and keeps
// carrying it for its full lifetime (~1 hour). Admin consent granted after
// that changes nothing the app can see, and MSAL's cache is persisted to
// disk, so restarting does not clear it either. The observed shape is an app
// returning 403 PermissionScopeNotGranted for the rest of a session, naming
// the exact scopes an administrator granted minutes earlier.
//
// Per cycle, for each ready tenant:
//   1. Ask Graph what the tenant has actually consented for the host's app
//      registration (ConsentedScopesReader). That is the only signal that
//      distinguishes "consented, token predates it" from "never consented".
//   2. If that read is unavailable - the normal case for a non-administrator,
//      whose account has no consent for the AdminTools scopes it needs - fall
//      back to comparing the token's own scp claim against the required scope
//      names. Less precise, still useful: it catches exactly the case a
//      refresh can fix.
//   3. Refresh what a refresh can fix; report once, then go quiet, for what
//      it cannot (TokenFreshnessGate).
//
// A healthy tenant costs one cached-token read per resource and produces no
// log line above Debug. Nothing here can prompt: every token path is the
// silent, DisableAutomaticAuthentication credential, and a tenant that needs
// interactive sign-in is left to TenantAuthHealthService, which owns that
// conversation with the user.
internal sealed class TokenFreshnessService : BackgroundService
{
    // Cap on the read half of a tenant's check (cached-token reads plus the
    // grants query). The refresh that may follow is bounded by its own
    // backoff schedule, so it is deliberately not covered by this.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);

    private readonly ITenantStore _tenantStore;
    private readonly ITenantReadinessTracker _readiness;
    private readonly ICredentialFactory _credentials;
    private readonly IAzureCloudConfig _cloud;
    private readonly IPluginLoader _pluginLoader;
    private readonly ConsentedScopesReader _consentedScopes;
    private readonly TokenFreshnessGate _gate;
    private readonly TokenFreshnessOptions _options;
    private readonly ILogger<TokenFreshnessService> _logger;

    // Built here rather than injected, as in SettingsViewModel: it is a
    // behaviour over the credential factory and the cloud endpoints, with no
    // state of its own worth registering.
    private readonly ConsentPropagationRefresher _refresher;

    public TokenFreshnessService(
        ITenantStore tenantStore,
        ITenantReadinessTracker readiness,
        ICredentialFactory credentials,
        IAzureCloudConfig cloud,
        IPluginLoader pluginLoader,
        ConsentedScopesReader consentedScopes,
        TokenFreshnessGate gate,
        IOptions<TokenFreshnessOptions> options,
        ILogger<TokenFreshnessService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _tenantStore = tenantStore;
        _readiness = readiness;
        _credentials = credentials;
        _cloud = cloud;
        _pluginLoader = pluginLoader;
        _consentedScopes = consentedScopes;
        _gate = gate;
        _options = options.Value;
        _logger = logger;

        _refresher = new ConsentPropagationRefresher(credentials, cloud, logger);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.CheckIntervalMinutes <= 0)
        {
            _logger.LogInformation(
                "Token freshness checks disabled (App:TokenFreshness:CheckIntervalMinutes <= 0).");
            return;
        }

        var interval = TimeSpan.FromMinutes(_options.CheckIntervalMinutes);
        var firstDelay = TimeSpan.FromSeconds(Math.Max(0, _options.FirstCheckDelaySeconds));
        _logger.LogInformation(
            "Token freshness checks enabled; first check in {FirstDelay}, then every {Interval}.",
            firstDelay, interval);

        // Short delay rather than a full interval: a token minted before a
        // consent change is already wrong at launch, so waiting half an hour
        // to notice would miss the whole first session. Long enough to stay
        // out of the startup burst's way.
        if (!await DelayAsync(firstDelay, stoppingToken).ConfigureAwait(false)) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAllTenantsAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // The loop outliving any single failure is the whole point:
                // one tenant's Graph hiccup must not end the checks for the
                // rest of the session.
                _logger.LogWarning(ex, "Token freshness check failed; will retry next interval.");
            }

            if (!await DelayAsync(interval, stoppingToken).ConfigureAwait(false)) return;
        }
    }

    private static async Task<bool> DelayAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task CheckAllTenantsAsync(CancellationToken stoppingToken)
    {
        // HostScopeSet.Runtime, not the provisioning set: this service judges
        // whether the tokens the app is being served are good enough to work,
        // and the AdminTools scopes are not part of that. They back the in-app
        // administration of app registrations only, a non-admin user will
        // legitimately never have them consented, and reporting them as
        // never-consented on the first check of every session is a warning
        // about nothing being broken.
        //
        // NullLogger, not this service's: the aggregator warns once per
        // malformed plugin declaration per call, and the provisioning paths
        // that can actually do something about one already report it.
        // Repeating those warnings every cycle would be precisely the
        // steady-state spam this service's gate exists to prevent.
        var required = RequiredPermissionsAggregator
            .Aggregate(_pluginLoader, NullLogger.Instance, HostScopeSet.Runtime)
            .Required;
        var byResource = GroupByResource(required);
        if (byResource.Count == 0) return;

        foreach (var ready in _readiness.ReadyTenants)
        {
            if (stoppingToken.IsCancellationRequested) return;

            // Only tenants the user still has switched on. A tenant whose
            // probe was disabled asked not to be chased about sign-in, and a
            // tenant absent from the store is mid-removal.
            var tenant = _tenantStore.FindByTenantId(ready.TenantId);
            if (tenant is null or { ProbeDisabled: true }) continue;

            try
            {
                await CheckTenantAsync(tenant, byResource, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex,
                    "Token freshness check for tenant {TenantId} was inconclusive; the other tenants are unaffected.",
                    tenant.TenantId);
            }
        }
    }

    private async Task CheckTenantAsync(
        Tenant tenant,
        IReadOnlyList<ResourceRequirements> byResource,
        CancellationToken stoppingToken)
    {
        // Scopes the token lacks that a refresh can supply, and scopes it
        // lacks because nobody ever consented them. Only the first kind is
        // worth any auth traffic.
        var refreshable = new List<PluginPermissionRequirement>();
        var unconsented = new List<PluginPermissionRequirement>();
        bool usedFallback;

        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            readCts.CancelAfter(ReadTimeout);
            var readToken = readCts.Token;

            var consented = await _consentedScopes.TryReadAsync(
                tenant.TenantId,
                tenant.ClientId,
                byResource.Select(r => r.ResourceAppId).ToArray(),
                readToken).ConfigureAwait(false);
            usedFallback = consented is null;

            foreach (var resource in byResource)
            {
                var tokenScopes = await ReadTokenScopesAsync(tenant.TenantId, resource.ResourceScope, readToken)
                    .ConfigureAwait(false);

                // Null is "cannot tell" - no token, or an unreadable one.
                // Access tokens are opaque by contract; treating that as
                // "the scope is missing" would refresh on a guess.
                if (tokenScopes is null) continue;

                var grantedForResource = consented?.GetValueOrDefault(resource.ResourceAppId);

                foreach (var requirement in resource.Requirements)
                {
                    if (tokenScopes.Contains(requirement.ScopeName)) continue;

                    // Without the grants read there is no way to tell the two
                    // apart, so the missing scope is assumed refreshable -
                    // one refresh attempt, then the gate goes quiet.
                    if (grantedForResource is null || grantedForResource.Contains(requirement.ScopeName))
                    {
                        refreshable.Add(requirement);
                    }
                    else
                    {
                        unconsented.Add(requirement);
                    }
                }
            }
        }

        var missingNames = refreshable.Concat(unconsented)
            .Select(r => r.ScopeName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (missingNames.Length == 0)
        {
            // The overwhelmingly common path: nothing missing, so nothing
            // happens at all. Re-arming costs nothing and makes sure a later
            // regression is reported rather than swallowed by an old verdict.
            _gate.Rearm(tenant.TenantId);
            return;
        }

        if (!_gate.ShouldAct(tenant.TenantId, missingNames))
        {
            _logger.LogDebug(
                "Tenant {TenantId} is still missing {Missing}; already reported, staying quiet until permissions or tokens are refreshed.",
                tenant.TenantId, Join(missingNames));
            return;
        }

        if (unconsented.Count > 0)
        {
            // Deliberately the one Warning this service can emit, and it
            // fires once per missing-scope set: refreshing a token cannot
            // conjure consent, so repeating it every cycle would be noise
            // about a condition only an administrator can clear.
            _logger.LogWarning(
                "Tenant {TenantId} {DisplayName} has not consented {Missing}. A token refresh cannot supply them - run Settings, Fix permissions as an administrator of that tenant. This is reported once per missing-scope set.",
                tenant.TenantId, tenant.DisplayName, JoinScopes(unconsented));
        }

        if (refreshable.Count == 0)
        {
            _gate.RecordActed(tenant.TenantId, missingNames);
            return;
        }

        _logger.LogDebug(
            "Tenant {TenantId} is serving an access token that predates the consent for {Missing}{Fallback}; force-refreshing.",
            tenant.TenantId,
            JoinScopes(refreshable),
            usedFallback ? " (consent state unreadable, judged from the token's scope claim)" : string.Empty);

        // The refresher already verifies against the token a plain silent
        // acquire serves and backs off across Entra's propagation delay; it
        // never throws and never prompts.
        var outcome = await _refresher.RefreshAsync(tenant.TenantId, refreshable, stoppingToken)
            .ConfigureAwait(false);

        if (outcome.State == ConsentRefreshState.Confirmed && unconsented.Count == 0)
        {
            // The auto-heal. Visible on purpose: this is the line that
            // explains why a tenant that was returning 403 suddenly works.
            _logger.LogInformation(
                "Refreshed tenant {TenantId} {DisplayName} access tokens; they now carry {Scopes}, which the cached tokens predated.",
                tenant.TenantId, tenant.DisplayName, JoinScopes(refreshable));
            _gate.Rearm(tenant.TenantId);
            return;
        }

        if (outcome.State != ConsentRefreshState.Confirmed)
        {
            // Every remaining state is either already reported by somebody
            // else (SignInRequired is TenantAuthHealthService's story) or
            // unknowable (Unverified means the token could not be read back).
            // Debug keeps the log honest without duplicating them.
            _logger.LogDebug(
                "Refreshing tenant {TenantId} did not resolve {Missing} (outcome {Outcome}); staying quiet until permissions or tokens are refreshed.",
                tenant.TenantId, Join(missingNames), outcome.State);
        }

        _gate.RecordActed(tenant.TenantId, missingNames);
    }

    // The scopes on the token every other caller in the app is being served
    // right now. Null means "cannot tell" - no silent token, or one whose
    // claims are unreadable.
    private async Task<IReadOnlySet<string>?> ReadTokenScopesAsync(
        string tenantId, string resourceScope, CancellationToken cancellationToken)
    {
        try
        {
            var credential = _credentials.GetForTenant(tenantId);
            var token = await credential
                .GetTokenAsync(new TokenRequestContext([resourceScope]), cancellationToken)
                .ConfigureAwait(false);

            var scopes = JwtAccessTokenScopes.TryRead(token.Token);
            return scopes is null
                ? null
                : new HashSet<string>(scopes, StringComparer.OrdinalIgnoreCase);
        }
        catch (AuthenticationRequiredException)
        {
            // The credential is configured never to prompt, and a background
            // service is the last place that should start one. The re-auth
            // conversation belongs to TenantAuthHealthService.
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex,
                "Could not read the cached token for tenant {TenantId} resource {Resource}.",
                tenantId, resourceScope);
            return null;
        }
    }

    // Requirements bucketed by the resource they belong to: the ".default"
    // scope tokens are acquired with, and the resource appId the consent
    // grants are keyed by. Declarations naming a resource the host holds no
    // tokens for are dropped - there is nothing to compare them against.
    private List<ResourceRequirements> GroupByResource(
        IReadOnlyList<PluginPermissionRequirement> required)
    {
        var groups = new List<ResourceRequirements>();
        foreach (var group in required
                     .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.ScopeName))
                     .GroupBy(r => r.Api))
        {
            string? resourceScope;
            string? resourceAppId;
            switch (group.Key)
            {
                case PermissionApi.MicrosoftGraph:
                    resourceScope = _cloud.GraphScope;
                    resourceAppId = AppRegistrationGraphClient.GraphResourceAppId;
                    break;
                case PermissionApi.AzureResourceManager:
                    resourceScope = _cloud.ArmScope;
                    resourceAppId = AppRegistrationGraphClient.ArmResourceAppId;
                    break;
                default:
                    // A plugin can declare an undefined enum value; it names
                    // no resource the host holds a token for.
                    continue;
            }

            groups.Add(new ResourceRequirements(resourceScope, resourceAppId, group.ToArray()));
        }
        return groups;
    }

    private static string JoinScopes(IEnumerable<PluginPermissionRequirement> requirements)
        => Join(requirements.Select(r => r.ScopeName));

    private static string Join(IEnumerable<string> names)
        => string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));

    private sealed record ResourceRequirements(
        string ResourceScope,
        string ResourceAppId,
        IReadOnlyList<PluginPermissionRequirement> Requirements);
}
