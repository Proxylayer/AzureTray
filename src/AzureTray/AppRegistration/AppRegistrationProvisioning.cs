using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.AppRegistration.Internal;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

public sealed class AppRegistrationProvisioning : IAppRegistrationProvisioning
{
    private readonly AppRegistrationGraphClient _graph;
    private readonly ILogger<AppRegistrationProvisioning> _logger;

    public AppRegistrationProvisioning(
        AppRegistrationGraphClient graph,
        ILogger<AppRegistrationProvisioning> logger)
    {
        _graph = graph;
        _logger = logger;
    }

    public async Task<AppRegistrationCreateResult> CreateAsync(
        string tenantId,
        string displayName,
        IReadOnlyList<PluginPermissionRequirement> required,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(required);

        // 1. POST /applications. Single-tenant, public-client, with the
        //    required scopes baked into requiredResourceAccess so the user
        //    doesn't need a separate Fix Permissions trip afterward.
        var rra = BuildRequiredResourceAccess(required);
        var createBody = new
        {
            displayName,
            signInAudience = "AzureADMyOrg",
            isFallbackPublicClient = true,
            requiredResourceAccess = rra,
        };

        var newApp = await _graph.PostAsync<GraphApplication>(tenantId, "v1.0/applications", createBody, cancellationToken)
            ?? throw new InvalidOperationException("Graph returned no body for the new application.");
        if (string.IsNullOrEmpty(newApp.Id) || string.IsNullOrEmpty(newApp.AppId))
        {
            throw new InvalidOperationException("Graph created the application but did not return its id/appId.");
        }
        var info = new AppRegistrationInfo(newApp.Id, newApp.AppId, displayName);

        _logger.LogInformation(
            "Created app registration {DisplayName} ({AppId}) in tenant {TenantId}.",
            displayName, newApp.AppId, tenantId);

        // 2. POST /servicePrincipals. Required before we can post consent grants.
        var sp = await _graph.PostAsync<GraphServicePrincipal>(
            tenantId,
            "v1.0/servicePrincipals",
            new { appId = newApp.AppId },
            cancellationToken)
            ?? throw new InvalidOperationException("Graph returned no body for the new service principal.");
        if (string.IsNullOrEmpty(sp.Id))
        {
            throw new InvalidOperationException("Graph created the service principal but did not return its id.");
        }

        // 3. PATCH the WAM broker redirect URI now that we know the appId.
        //    Done as a best-effort: the app is already created; if this
        //    fails the user can re-run Fix Permissions / re-add the URI
        //    manually.
        var brokerAdded = false;
        try
        {
            var brokerUri = $"ms-appx-web://microsoft.aad.brokerplugin/{newApp.AppId}";
            await _graph.PatchAsync(
                tenantId,
                $"v1.0/applications/{newApp.Id}",
                new { publicClient = new { redirectUris = new[] { brokerUri } } },
                cancellationToken);
            brokerAdded = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Created app {AppId} but failed to add WAM broker redirect URI. Cold-start silent SSO will not work until 'ms-appx-web://microsoft.aad.brokerplugin/{AppId}' is added.",
                newApp.AppId, newApp.AppId);
        }

        // 4. Grant admin consent for each required scope per resource.
        var scopesGranted = 0;
        foreach (var resourceGroup in required.GroupBy(r => AppRegistrationGraphClient.GetResourceAppId(r.Api)))
        {
            var resourceSp = await _graph.GetServicePrincipalByAppIdAsync(tenantId, resourceGroup.Key, cancellationToken);
            if (resourceSp?.Id is null)
            {
                _logger.LogWarning(
                    "Resource service principal for {ResourceAppId} not provisioned in tenant {TenantId}; skipping admin consent for that resource.",
                    resourceGroup.Key, tenantId);
                continue;
            }

            var scopeNames = resourceGroup.Select(r => r.ScopeName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var scopeString = string.Join(" ", scopeNames);

            await _graph.PostAsync<OAuth2PermissionGrant>(
                tenantId,
                "v1.0/oauth2PermissionGrants",
                new
                {
                    clientId = sp.Id,
                    consentType = "AllPrincipals",
                    principalId = (string?)null,
                    resourceId = resourceSp.Id,
                    scope = scopeString,
                },
                cancellationToken);

            scopesGranted += scopeNames.Count;
        }

        _logger.LogInformation(
            "Admin-consented {Count} scope(s) on new app {AppId} in tenant {TenantId}.",
            scopesGranted, newApp.AppId, tenantId);

        return new AppRegistrationCreateResult(info, scopesGranted, brokerAdded);
    }

    private static List<RequiredResourceAccessDto> BuildRequiredResourceAccess(
        IReadOnlyList<PluginPermissionRequirement> required)
    {
        var result = new List<RequiredResourceAccessDto>();
        foreach (var group in required.GroupBy(r => AppRegistrationGraphClient.GetResourceAppId(r.Api)))
        {
            var access = new List<ResourceAccessDto>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var req in group)
            {
                if (seen.Add(req.ScopeId))
                {
                    access.Add(new ResourceAccessDto(req.ScopeId, "Scope"));
                }
            }
            result.Add(new RequiredResourceAccessDto(group.Key, access));
        }
        return result;
    }

    public async Task<bool> EnsureBrokerRedirectUriAsync(
        string tenantId,
        string appClientId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appClientId);

        var brokerUri = $"ms-appx-web://microsoft.aad.brokerplugin/{appClientId}";

        var url = $"v1.0/applications?$filter=appId eq '{AppRegistrationGraphClient.EscapeFilter(appClientId)}'&$select=id,appId,publicClient&$top=1";
        var page = await _graph.GetAsync<ODataPage<GraphApplication>>(tenantId, url, cancellationToken);
        var app = page?.Value?.FirstOrDefault();
        if (app?.Id is null)
        {
            _logger.LogWarning(
                "Cannot ensure broker redirect URI: app registration {AppId} not found in tenant {TenantId}.",
                appClientId, tenantId);
            return false;
        }

        var current = app.PublicClient?.RedirectUris ?? new List<string>();
        if (current.Contains(brokerUri, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "Broker redirect URI already present on app {AppId} in tenant {TenantId}.",
                appClientId, tenantId);
            return true;
        }

        var updated = new List<string>(current) { brokerUri };
        await _graph.PatchAsync(
            tenantId,
            $"v1.0/applications/{app.Id}",
            new { publicClient = new { redirectUris = updated } },
            cancellationToken);

        _logger.LogInformation(
            "Added broker redirect URI to app {AppId} in tenant {TenantId} so WAM silent SSO can succeed on cold start.",
            appClientId, tenantId);
        return true;
    }
}
