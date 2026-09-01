using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.AppRegistration.Internal;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

public sealed class AppRegistrationPermissions : IAppRegistrationPermissions
{
    private readonly AppRegistrationGraphClient _graph;
    private readonly ILogger<AppRegistrationPermissions> _logger;

    public AppRegistrationPermissions(
        AppRegistrationGraphClient graph,
        ILogger<AppRegistrationPermissions> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    public async Task<PermissionCheckResult> CheckAsync(
        string tenantId,
        string appClientId,
        IReadOnlyList<PluginPermissionRequirement> required,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appClientId);
        ArgumentNullException.ThrowIfNull(required);

        required = RequiredPermissionsAggregator.KeepValid(required, _logger);
        if (required.Count == 0)
        {
            return new PermissionCheckResult([], []);
        }

        var app = await _graph.GetAppByClientIdAsync(tenantId, appClientId, cancellationToken);
        if (app is null)
        {
            // App registration not found at all — every requirement is missing and unconsented.
            return new PermissionCheckResult(required, required);
        }

        var missing = ComputeMissingScopes(app, required);

        var appSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, appClientId, cancellationToken);
        var notConsented = appSp is null
            ? required.ToList()
            : await ComputeUnconsentedAsync(tenantId, appSp.Id!, required, cancellationToken);

        return new PermissionCheckResult(missing, notConsented);
    }

    // Replace semantics — matches legacy FixPermissionsAsync. The app's
    // requiredResourceAccess (for resources we manage) is rebuilt from the
    // current `required` list, pruning any scopes/resources that aren't
    // requested. Per-resource oauth2PermissionGrant scope strings are
    // likewise replaced with the exact required scope set so stale
    // permissions don't accumulate as plugins are added/removed.
    public async Task<PermissionFixResult> EnsureAsync(
        string tenantId,
        string appClientId,
        IReadOnlyList<PluginPermissionRequirement> required,
        IReadOnlyList<RejectedRequirement> unprovisionable,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appClientId);
        ArgumentNullException.ThrowIfNull(required);
        ArgumentNullException.ThrowIfNull(unprovisionable);

        // A ScopeId that isn't a GUID makes Graph reject the whole
        // requiredResourceAccess PATCH ("Cannot convert the literal ... to
        // the expected type 'Edm.Guid'"), taking the host's and every other
        // plugin's scopes down with it. Drop those before we build the body.
        required = RequiredPermissionsAggregator.KeepValid(required, _logger);
        if (required.Count == 0)
        {
            return new PermissionFixResult([], [], 0, 0);
        }

        // Resources whose cleanup must be switched off. A dropped declaration
        // is only unprovisionable, never withdrawn: its scope name cannot be
        // matched against the GUID-keyed resourceAccess entries on the app,
        // so the only way to be certain we never revoke it is to leave
        // everything already present for that resource alone.
        var protectedResources = ResolveProtectedResources(unprovisionable, tenantId);

        var app = await _graph.GetAppByClientIdAsync(tenantId, appClientId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"App registration with appId '{appClientId}' not found in tenant '{tenantId}'.");

        var appSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, appClientId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Service principal for appId '{appClientId}' not found in tenant '{tenantId}'.");

        // 1. Rebuild requiredResourceAccess from scratch for each managed
        //    resource; scopes for resources we don't list are pruned.
        var (newRra, scopesAdded, staleScopesRemoved) =
            ComputeReplacementRequiredResourceAccess(app.RequiredResourceAccess, required, protectedResources);

        if (scopesAdded.Count > 0 || staleScopesRemoved > 0)
        {
            await _graph.PatchAsync(tenantId, $"v1.0/applications/{app.Id}", new { requiredResourceAccess = newRra }, cancellationToken);
            _logger.LogInformation(
                "Updated app {AppClientId} in tenant {TenantId}: added {Added} scope(s), removed {Removed} stale scope(s).",
                appClientId, tenantId, scopesAdded.Count, staleScopesRemoved);
        }

        // 2. Replace each per-resource oauth2PermissionGrant scope string with
        //    exactly the required scopes for that resource.
        var grants = await _graph.ListAsync<OAuth2PermissionGrant>(
            tenantId,
            $"v1.0/oauth2PermissionGrants?$filter=clientId eq '{AppRegistrationGraphClient.EscapeFilter(appSp.Id!)}'",
            cancellationToken);

        var grantsAdded = new List<PluginPermissionRequirement>();
        var staleGrantsRemoved = 0;

        foreach (var resourceGroup in required.GroupBy(r => AppRegistrationGraphClient.GetResourceAppId(r.Api)))
        {
            var resourceAppId = resourceGroup.Key;
            var resourceSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, resourceAppId, cancellationToken);
            if (resourceSp?.Id is null)
            {
                _logger.LogWarning(
                    "Service principal for resource {ResourceAppId} not provisioned in tenant {TenantId}; cannot consent.",
                    resourceAppId, tenantId);
                continue;
            }

            var existingGrant = grants.FirstOrDefault(g => g.ResourceId == resourceSp.Id);
            var existingScopes = SplitScopes(existingGrant?.Scope);
            var requiredScopeNames = resourceGroup.Select(r => r.ScopeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var requiredScopesSet = new HashSet<string>(requiredScopeNames, StringComparer.OrdinalIgnoreCase);

            // Protected resource: fold whatever is already consented into the
            // scope string we write back, so the PATCH can only ever be
            // additive. Nothing counts as stale here by construction.
            if (protectedResources.Contains(resourceAppId))
            {
                foreach (var granted in existingScopes)
                {
                    if (requiredScopesSet.Add(granted)) requiredScopeNames.Add(granted);
                }
            }

            var newlyConsented = resourceGroup.Where(r => !existingScopes.Contains(r.ScopeName)).ToList();
            var staleForResource = existingScopes.Count(s => !requiredScopesSet.Contains(s));

            // No-op if the grant already has exactly the required set.
            if (newlyConsented.Count == 0 && staleForResource == 0) continue;

            var scopeString = string.Join(" ", requiredScopeNames);

            if (existingGrant?.Id is not null)
            {
                await _graph.PatchAsync(tenantId,
                    $"v1.0/oauth2PermissionGrants/{existingGrant.Id}",
                    new { scope = scopeString },
                    cancellationToken);
            }
            else
            {
                await _graph.PostAsync<OAuth2PermissionGrant>(tenantId,
                    "v1.0/oauth2PermissionGrants",
                    new
                    {
                        clientId = appSp.Id,
                        consentType = "AllPrincipals",
                        principalId = (string?)null,
                        resourceId = resourceSp.Id,
                        scope = scopeString,
                    },
                    cancellationToken);
            }

            grantsAdded.AddRange(newlyConsented);
            staleGrantsRemoved += staleForResource;
        }

        // 3. Prune grants for resources that are no longer requested at all.
        //    These linger from removed plugins and would otherwise grant
        //    permissions the user no longer expects.
        var requiredResourceAppIds = required.Select(r => AppRegistrationGraphClient.GetResourceAppId(r.Api))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var grant in grants)
        {
            if (grant.Id is null || grant.ResourceId is null) continue;
            if (grant.Scope is null || grant.Scope.Length == 0) continue;

            // Find which resource appId this grant points to. We look up
            // by resourceId (the SP object id), not by appId, so we need
            // the reverse mapping. Easier: skip if the resource SP has an
            // appId that's in our required set.
            var resourceSp = await _graph.GetServicePrincipalByObjectIdAsync(tenantId, grant.ResourceId, cancellationToken);
            if (resourceSp?.AppId is null) continue;
            if (requiredResourceAppIds.Contains(resourceSp.AppId)) continue;

            // A resource that only a rejected declaration named appears in
            // neither list; deleting its grant would revoke access nobody
            // asked us to revoke.
            if (protectedResources.Contains(resourceSp.AppId))
            {
                _logger.LogInformation(
                    "Leaving the oauth2PermissionGrant for resource {ResourceAppId} in tenant {TenantId} in place: a declaration for that resource could not be provisioned, so its consent is not treated as stale.",
                    resourceSp.AppId, tenantId);
                continue;
            }

            // This grant is for a resource we no longer manage. Prune it.
            var pruned = SplitScopes(grant.Scope).Count;
            await _graph.DeleteAsync(tenantId, $"v1.0/oauth2PermissionGrants/{grant.Id}", cancellationToken);
            staleGrantsRemoved += pruned;
        }

        if (grantsAdded.Count > 0 || staleGrantsRemoved > 0)
        {
            _logger.LogInformation(
                "Adjusted admin consent on app {AppClientId} in tenant {TenantId}: added {Added} scope grant(s), removed {Removed} stale scope grant(s).",
                appClientId, tenantId, grantsAdded.Count, staleGrantsRemoved);
        }

        return new PermissionFixResult(scopesAdded, grantsAdded, staleScopesRemoved, staleGrantsRemoved);
    }

    private static List<PluginPermissionRequirement> ComputeMissingScopes(
        GraphApplication app,
        IReadOnlyList<PluginPermissionRequirement> required)
    {
        var missing = new List<PluginPermissionRequirement>();

        foreach (var group in required.GroupBy(r => AppRegistrationGraphClient.GetResourceAppId(r.Api)))
        {
            var resourceGroup = app.RequiredResourceAccess?.FirstOrDefault(r => r.ResourceAppId == group.Key);
            var declaredIds = new HashSet<string>(
                resourceGroup?.ResourceAccess?
                    .Where(ra => !string.IsNullOrWhiteSpace(ra.Id))
                    .Select(ra => ra.Id!) ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            foreach (var req in group)
            {
                if (!declaredIds.Contains(req.ScopeId))
                {
                    missing.Add(req);
                }
            }
        }

        return missing;
    }

    private async Task<List<PluginPermissionRequirement>> ComputeUnconsentedAsync(
        string tenantId,
        string appSpObjectId,
        IReadOnlyList<PluginPermissionRequirement> required,
        CancellationToken ct)
    {
        var grants = await _graph.ListAsync<OAuth2PermissionGrant>(
            tenantId,
            $"v1.0/oauth2PermissionGrants?$filter=clientId eq '{AppRegistrationGraphClient.EscapeFilter(appSpObjectId)}'",
            ct);

        var unconsented = new List<PluginPermissionRequirement>();
        foreach (var group in required.GroupBy(r => AppRegistrationGraphClient.GetResourceAppId(r.Api)))
        {
            var resourceSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, group.Key, ct);
            if (resourceSp?.Id is null)
            {
                unconsented.AddRange(group);
                continue;
            }

            var grant = grants.FirstOrDefault(g => g.ResourceId == resourceSp.Id);
            var grantedScopes = SplitScopes(grant?.Scope);

            foreach (var req in group)
            {
                if (!grantedScopes.Contains(req.ScopeName))
                {
                    unconsented.Add(req);
                }
            }
        }
        return unconsented;
    }

    // Builds the new requiredResourceAccess array from scratch using only
    // the current required scopes. Resources not represented in `required`
    // are dropped entirely; within a managed resource, scopes that aren't
    // in `required` are dropped. Returns the new list plus the counts of
    // newly-added and pruned scopes for reporting.
    private static (List<RequiredResourceAccessDto> NewRra, List<PluginPermissionRequirement> ScopesAdded, int StaleRemoved) ComputeReplacementRequiredResourceAccess(
        List<RequiredResourceAccessDto>? current,
        IReadOnlyList<PluginPermissionRequirement> required,
        HashSet<string> protectedResources)
    {
        var newRra = new List<RequiredResourceAccessDto>();
        var scopesAdded = new List<PluginPermissionRequirement>();
        var staleRemoved = 0;

        var requiredByResource = required
            .GroupBy(r => AppRegistrationGraphClient.GetResourceAppId(r.Api))
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        foreach (var (resourceAppId, reqs) in requiredByResource)
        {
            var existingResource = current?.FirstOrDefault(r => r.ResourceAppId == resourceAppId);
            var existingIds = new HashSet<string>(
                existingResource?.ResourceAccess?.Where(ra => ra.Id is not null).Select(ra => ra.Id!) ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);

            var newAccess = new List<ResourceAccessDto>();
            var requiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var req in reqs)
            {
                if (requiredIds.Add(req.ScopeId))
                {
                    newAccess.Add(new ResourceAccessDto(req.ScopeId, "Scope"));
                    if (!existingIds.Contains(req.ScopeId)) scopesAdded.Add(req);
                }
            }

            if (existingResource?.ResourceAccess is { } ra)
            {
                var notRequired = ra.Where(x => x.Id is not null && !requiredIds.Contains(x.Id)).ToList();
                if (protectedResources.Contains(resourceAppId))
                {
                    // Carry every existing entry through unchanged. One of
                    // them may well be the GUID behind the malformed
                    // declaration; we cannot tell which, so none of them go.
                    foreach (var keep in notRequired)
                    {
                        newAccess.Add(keep);
                        requiredIds.Add(keep.Id!);
                    }
                }
                else
                {
                    staleRemoved += notRequired.Count;
                }
            }

            newRra.Add(new RequiredResourceAccessDto(resourceAppId, newAccess));
        }

        if (current is not null)
        {
            foreach (var existingResource in current)
            {
                if (existingResource.ResourceAppId is null) continue;
                if (requiredByResource.ContainsKey(existingResource.ResourceAppId)) continue;

                // Protected resources keep their whole block: the only
                // declaration naming them was one we could not provision.
                if (protectedResources.Contains(existingResource.ResourceAppId))
                {
                    newRra.Add(existingResource);
                    continue;
                }

                if (existingResource.ResourceAccess is not null)
                {
                    staleRemoved += existingResource.ResourceAccess.Count;
                }
            }
        }

        return (newRra, scopesAdded, staleRemoved);
    }

    // Maps rejected declarations onto the resources whose stale cleanup has
    // to be switched off. Deliberately coarse: protecting the whole resource
    // needs no extra Graph round-trip and has no failure mode, whereas
    // resolving each malformed scope *name* to its GUID via the resource
    // service principal's oauth2PermissionScopes could fail (throttling, a
    // missing service principal, a scope the resource no longer publishes)
    // and every one of those failures would end in exactly the outcome we
    // are guarding against - a revoke. The price of being coarse is that
    // genuinely stale scopes on that resource survive until the plugin fixes
    // its declaration, which is the strictly safer way to be wrong.
    private HashSet<string> ResolveProtectedResources(
        IReadOnlyList<RejectedRequirement> unprovisionable, string tenantId)
    {
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rejected in unprovisionable)
        {
            string resourceAppId;
            try
            {
                resourceAppId = AppRegistrationGraphClient.GetResourceAppId(rejected.Api);
            }
            catch (ArgumentOutOfRangeException)
            {
                // A plugin can hand us an undefined enum value; it names no
                // resource we manage, so there is nothing to protect.
                continue;
            }

            resources.Add(resourceAppId);
            _logger.LogWarning(
                "Stale cleanup disabled for resource {ResourceAppId} in tenant {TenantId}: {Source} declares {ScopeName} with ScopeId '{ScopeId}', which is not a GUID. " +
                "The declaration cannot be provisioned, and its scope name cannot be matched against the app registration's scope ids, so existing scopes and grants for that resource are left exactly as they are.",
                resourceAppId, tenantId, rejected.Source, rejected.ScopeName, rejected.ScopeId);
        }
        return resources;
    }

    private static HashSet<string> SplitScopes(string? scopeString)
        => new(scopeString?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>(),
            StringComparer.OrdinalIgnoreCase);
}
