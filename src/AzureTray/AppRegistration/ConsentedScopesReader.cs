using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.AppRegistration.Internal;

namespace AzureTray.AppRegistration;

// Reads what a tenant has actually consented for the host's app
// registration: the delegated scope names on the oauth2PermissionGrants
// whose client is that app's service principal, grouped by resource.
//
// This is the signal that separates the two failure modes a stale token
// produces. "Consented, but the cached token predates the grant" is healed
// by a refresh; "never consented" cannot be healed by any number of
// refreshes and needs an administrator. Comparing a token's scp claim
// against the required list alone cannot tell them apart.
//
// Reading grants needs the AdminTools scopes (Application.ReadWrite.All +
// DelegatedPermissionGrant.ReadWrite.All), which only administrators have
// consented — so every failure here, 403 included, returns null and the
// caller falls back. Null is always "cannot tell", never "nothing is
// consented": concluding the latter from a permissions error would turn a
// non-admin's ordinary state into a permanent warning.
//
// The read path deliberately mirrors AppRegistrationPermissions'
// ComputeUnconsentedAsync — same filter, same resource-service-principal
// lookup — so there is one way in this codebase to ask Graph this question.
internal sealed class ConsentedScopesReader
{
    private readonly AppRegistrationGraphClient _graph;
    private readonly ILogger<ConsentedScopesReader> _logger;

    public ConsentedScopesReader(
        AppRegistrationGraphClient graph,
        ILogger<ConsentedScopesReader> logger)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(logger);

        _graph = graph;
        _logger = logger;
    }

    // Consented delegated scope names keyed by resource appId, or null when
    // the tenant's consent could not be read at all. A resource present in
    // the result with an empty set genuinely has no consented scopes; a null
    // result says nothing about any resource.
    public async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>?> TryReadAsync(
        string tenantId,
        string? appClientId,
        IReadOnlyCollection<string> resourceAppIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(resourceAppIds);

        if (string.IsNullOrWhiteSpace(appClientId))
        {
            // The tenant runs on the shared fallback client id, which has no
            // app registration object of its own here — there is no service
            // principal to filter the grants by.
            _logger.LogDebug(
                "Tenant {TenantId} has no dedicated app registration, so its consented scopes cannot be read.",
                tenantId);
            return null;
        }

        try
        {
            var appSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, appClientId, cancellationToken)
                .ConfigureAwait(false);
            if (appSp?.Id is null)
            {
                _logger.LogDebug(
                    "No service principal for app {AppClientId} in tenant {TenantId}; consented scopes cannot be read.",
                    appClientId, tenantId);
                return null;
            }

            var grants = await _graph.ListAsync<OAuth2PermissionGrant>(
                tenantId,
                $"v1.0/oauth2PermissionGrants?$filter=clientId eq '{AppRegistrationGraphClient.EscapeFilter(appSp.Id)}'",
                cancellationToken).ConfigureAwait(false);

            var byResource = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var resourceAppId in resourceAppIds.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var resourceSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, resourceAppId, cancellationToken)
                    .ConfigureAwait(false);

                // No service principal for the resource means the tenant has
                // never consented anything on it — an accurate empty set,
                // not a failure to read.
                var consented = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (resourceSp?.Id is not null)
                {
                    // Every grant for this resource counts, tenant-wide
                    // (AllPrincipals) and per-user alike. A per-user grant
                    // belonging to somebody else would be read as consent the
                    // signed-in user does not have — the cost of that is one
                    // refresh that changes nothing, whereas ignoring user
                    // grants would mis-report the single-user consent path as
                    // never-consented.
                    foreach (var grant in grants.Where(g => g.ResourceId == resourceSp.Id))
                    {
                        foreach (var scope in SplitScopes(grant.Scope))
                        {
                            consented.Add(scope);
                        }
                    }
                }

                byResource[resourceAppId] = consented;
            }

            return byResource;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Expected for every non-administrator: the grants read is gated
            // on scopes only admins have consented. Debug, not Warning — this
            // is a supported mode, and the caller has a fallback.
            _logger.LogDebug(ex,
                "Could not read the consented scopes for tenant {TenantId} app {AppClientId}; falling back to the token's own scope claim.",
                tenantId, appClientId);
            return null;
        }
    }

    private static string[] SplitScopes(string? scopeString)
        => scopeString?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
}
