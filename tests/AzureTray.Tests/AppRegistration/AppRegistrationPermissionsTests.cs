using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using AzureTray.AppRegistration;
using AzureTray.Plugin.Contracts;
using Xunit;
using static AzureTray.Tests.AppRegistration.AppRegistrationTestFixtures;

namespace AzureTray.Tests.AppRegistration;

public sealed class AppRegistrationPermissionsTests
{
    [Fact]
    public async Task CheckAsync_ReportsAllMissing_WhenAppNotFound()
    {
        var handler = new RoutedHttpHandler();
        handler.OnGet("https://graph.microsoft.com/v1.0/applications", _ => Json(@"{ ""value"": [] }"));

        var permissions = NewPermissions(handler);
        var required = new[] { GraphRequirement("User.Read", UserReadScopeId) };

        var result = await permissions.CheckAsync("tenant-1", "client-1", required, CancellationToken.None);

        Assert.Equal(required.Length, result.Missing.Count);
        Assert.Equal(required.Length, result.NotConsented.Count);
    }

    [Fact]
    public async Task CheckAsync_ReportsMissingScopes_AndUnconsentedScopes()
    {
        var handler = new RoutedHttpHandler();

        // app lookup: has User.Read declared but NOT RoleManagement.Read.Directory
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/applications" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json($$"""
                {
                  "value": [{
                    "id": "app-obj-1",
                    "appId": "client-1",
                    "displayName": "Our App",
                    "requiredResourceAccess": [{
                      "resourceAppId": "{{GraphResourceAppId}}",
                      "resourceAccess": [{ "id": "{{UserReadScopeId}}", "type": "Scope" }]
                    }]
                  }]
                }
                """));

        // SP lookup for our app
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json("""
                { "value": [{ "id": "our-sp-obj-1", "appId": "client-1", "displayName": "Our App" }] }
                """));

        // SP lookup for Graph
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj-1", "appId": "{{GraphResourceAppId}}", "displayName": "Microsoft Graph" }] }
                """));

        // Grants: only User.Read consented
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                {
                  "value": [{
                    "id": "grant-1",
                    "clientId": "our-sp-obj-1",
                    "consentType": "AllPrincipals",
                    "resourceId": "graph-sp-obj-1",
                    "scope": "User.Read",
                    "principalId": null
                  }]
                }
                """));

        var permissions = NewPermissions(handler);
        var required = new[]
        {
            GraphRequirement("User.Read", UserReadScopeId),
            GraphRequirement("RoleManagement.Read.Directory", RoleManagementReadDirectoryScopeId),
        };

        var result = await permissions.CheckAsync("tenant-1", "client-1", required, CancellationToken.None);

        Assert.Single(result.Missing);
        Assert.Equal("RoleManagement.Read.Directory", result.Missing[0].ScopeName);
        Assert.Single(result.NotConsented);
        Assert.Equal("RoleManagement.Read.Directory", result.NotConsented[0].ScopeName);
        Assert.False(result.IsFullyConfigured);
    }

    [Fact]
    public async Task CheckAsync_ReportsFullyConfigured_WhenScopesPresentAndConsented()
    {
        var handler = new RoutedHttpHandler();

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/applications" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json($$"""
                {
                  "value": [{
                    "id": "app-obj-1",
                    "appId": "client-1",
                    "displayName": "Our App",
                    "requiredResourceAccess": [{
                      "resourceAppId": "{{GraphResourceAppId}}",
                      "resourceAccess": [{ "id": "{{UserReadScopeId}}", "type": "Scope" }]
                    }]
                  }]
                }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json("""
                { "value": [{ "id": "our-sp-obj-1", "appId": "client-1" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj-1", "appId": "{{GraphResourceAppId}}" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                {
                  "value": [{
                    "id": "grant-1",
                    "clientId": "our-sp-obj-1",
                    "resourceId": "graph-sp-obj-1",
                    "scope": "User.Read"
                  }]
                }
                """));

        var permissions = NewPermissions(handler);
        var required = new[] { GraphRequirement("User.Read", UserReadScopeId) };

        var result = await permissions.CheckAsync("tenant-1", "client-1", required, CancellationToken.None);

        Assert.True(result.IsFullyConfigured);
        Assert.Empty(result.Missing);
        Assert.Empty(result.NotConsented);

        // Non-vacuity guard. An empty required list also reports "fully
        // configured" - and that is exactly what a fixture with non-GUID
        // scope ids degrades into, since KeepValid filters it away and
        // CheckAsync returns before it talks to Graph at all. If the routes
        // below were never called, the three assertions above proved nothing.
        Assert.Contains(handler.Recorded, r => r.Uri.AbsolutePath == "/v1.0/oauth2PermissionGrants");
    }

    [Fact]
    public async Task CheckAsync_EmptyRequired_ReturnsFullyConfigured()
    {
        var permissions = NewPermissions(new RoutedHttpHandler());
        var result = await permissions.CheckAsync("tenant-1", "client-1", Array.Empty<PluginPermissionRequirement>(), CancellationToken.None);
        Assert.True(result.IsFullyConfigured);
    }

    [Fact]
    public async Task EnsureAsync_ReplacesScopes_PatchesRequiredResourceAccess_AndCountsStale()
    {
        var handler = new RoutedHttpHandler();

        // App has User.Read AND a stale scope (RoleManagement.Read.Directory).
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/applications" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json($$"""
                {
                  "value": [{
                    "id": "app-obj-1",
                    "appId": "client-1",
                    "displayName": "Our App",
                    "requiredResourceAccess": [{
                      "resourceAppId": "{{GraphResourceAppId}}",
                      "resourceAccess": [
                        { "id": "{{UserReadScopeId}}",  "type": "Scope" },
                        { "id": "{{RoleManagementReadDirectoryScopeId}}",  "type": "Scope" }
                      ]
                    }]
                  }]
                }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json("""
                { "value": [{ "id": "our-sp-obj-1", "appId": "client-1" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }] }
                """));

        // Existing grant: BOTH scopes already consented, including the stale one.
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                {
                  "value": [{
                    "id": "grant-1",
                    "clientId": "our-sp-obj-1",
                    "resourceId": "graph-sp-obj",
                    "scope": "User.Read RoleManagement.Read.Directory"
                  }]
                }
                """));

        // Capture PATCHes so we can assert what was sent.
        handler.OnPatch(
            url => url.AbsolutePath == "/v1.0/applications/app-obj-1",
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        handler.OnPatch(
            url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants/grant-1",
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var permissions = NewPermissions(handler);
        var required = new[] { GraphRequirement("User.Read", UserReadScopeId) };

        var result = await permissions.EnsureAsync("tenant-1", "client-1", required, [], CancellationToken.None);

        // No new scopes/grants since User.Read was already declared and consented.
        Assert.Empty(result.ScopesAdded);
        Assert.Empty(result.GrantsAdded);
        // But the stale ones got pruned.
        Assert.Equal(1, result.StaleScopesRemoved);
        Assert.Equal(1, result.StaleGrantsRemoved);

        // Verify the RRA PATCH dropped the stale scope.
        var rraPatch = handler.Recorded.Single(r => r.Method == HttpMethod.Patch && r.Uri.AbsolutePath == "/v1.0/applications/app-obj-1");
        Assert.NotNull(rraPatch.Body);
        Assert.Contains($"\"{UserReadScopeId}\"", rraPatch.Body);
        Assert.DoesNotContain($"\"{RoleManagementReadDirectoryScopeId}\"", rraPatch.Body);

        // Verify the grant PATCH replaced the scope string with exactly the required scope.
        var grantPatch = handler.Recorded.Single(r => r.Method == HttpMethod.Patch && r.Uri.AbsolutePath == "/v1.0/oauth2PermissionGrants/grant-1");
        Assert.NotNull(grantPatch.Body);
        Assert.Contains("\"scope\":\"User.Read\"", grantPatch.Body);
        Assert.DoesNotContain("RoleManagement", grantPatch.Body);
    }

    // Regression guard for the incident this protection exists for: a plugin
    // declared a scope *name* where a scope GUID belongs, the declaration was
    // filtered out of the required list, and "not required" then read as
    // "stale" - so the host stripped scopes the tenant had already consented
    // to out of the app registration and its grant. A rejected declaration is
    // unprovisionable, never withdrawn: nothing for that resource may be
    // touched.
    [Fact]
    public async Task EnsureAsync_RemovesNothing_WhenADeclarationForThatResourceWasRejected()
    {
        var handler = new RoutedHttpHandler();

        // The app declares User.Read plus the scope behind the malformed
        // declaration (which we can only see as a GUID here - the rejected
        // declaration carries a name, so it cannot be matched against it).
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/applications" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json($$"""
                {
                  "value": [{
                    "id": "app-obj-1",
                    "appId": "client-1",
                    "displayName": "Our App",
                    "requiredResourceAccess": [{
                      "resourceAppId": "{{GraphResourceAppId}}",
                      "resourceAccess": [
                        { "id": "{{UserReadScopeId}}",  "type": "Scope" },
                        { "id": "{{RoleManagementReadDirectoryScopeId}}",  "type": "Scope" }
                      ]
                    }]
                  }]
                }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json("""
                { "value": [{ "id": "our-sp-obj-1", "appId": "client-1" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals/graph-sp-obj",
            _ => Json($$"""
                { "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }
                """));

        // Both scopes are already consented; the second one only because of
        // the declaration we could not provision.
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                {
                  "value": [{
                    "id": "grant-1",
                    "clientId": "our-sp-obj-1",
                    "resourceId": "graph-sp-obj",
                    "scope": "User.Read RoleManagement.Read.Directory"
                  }]
                }
                """));

        // Routed so a wrongly-issued write is recorded and asserted on,
        // rather than dying as an unmatched route.
        handler.OnPatch(url => url.AbsolutePath == "/v1.0/applications/app-obj-1", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.OnPatch(url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants/grant-1", _ => new HttpResponseMessage(HttpStatusCode.NoContent));
        handler.OnDelete(url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants/grant-1", _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var permissions = NewPermissions(handler);
        var required = new[] { GraphRequirement("User.Read", UserReadScopeId) };
        var rejected = new[]
        {
            // What a plugin actually shipped: the scope name in the id slot.
            new RejectedRequirement("plugin-x", PermissionApi.MicrosoftGraph, "RoleManagement.Read.Directory", "RoleManagement.Read.Directory"),
        };

        var result = await permissions.EnsureAsync("tenant-1", "client-1", required, rejected, CancellationToken.None);

        // Identical inputs minus the rejection produce 1 stale scope + 1 stale
        // grant (see EnsureAsync_ReplacesScopes_...); the rejection must make
        // that nothing at all.
        Assert.Equal(0, result.StaleScopesRemoved);
        Assert.Equal(0, result.StaleGrantsRemoved);
        Assert.Empty(result.ScopesAdded);
        Assert.Empty(result.GrantsAdded);

        // Nothing was written: no RRA rewrite, no grant rewrite, no delete.
        Assert.DoesNotContain(handler.Recorded, r => r.Method == HttpMethod.Patch);
        Assert.DoesNotContain(handler.Recorded, r => r.Method == HttpMethod.Delete);

        // ...and it got far enough to have been able to write. Without this
        // the assertions above would also hold for an EnsureAsync that bailed
        // out before reaching Graph.
        Assert.Contains(handler.Recorded, r => r.Uri.AbsolutePath == "/v1.0/oauth2PermissionGrants");
    }

    // Same protection one layer out: the rejected declaration names a
    // resource nothing else requires, so the resource appears in neither the
    // required nor the stale list. Its grant must survive.
    [Fact]
    public async Task EnsureAsync_KeepsGrant_ForResourceNamedOnlyByRejectedDeclaration()
    {
        var handler = new RoutedHttpHandler();

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/applications" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json($$"""
                {
                  "value": [{
                    "id": "app-obj-1",
                    "appId": "client-1",
                    "displayName": "Our App",
                    "requiredResourceAccess": [{
                      "resourceAppId": "{{GraphResourceAppId}}",
                      "resourceAccess": [{ "id": "{{UserReadScopeId}}", "type": "Scope" }]
                    }]
                  }]
                }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains("appId eq 'client-1'"),
            _ => Json("""
                { "value": [{ "id": "our-sp-obj-1", "appId": "client-1" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(url.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }] }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals/graph-sp-obj",
            _ => Json($$"""
                { "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }
                """));

        // The ARM service principal the surviving grant points at.
        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/servicePrincipals/arm-sp-obj",
            _ => Json($$"""
                { "id": "arm-sp-obj", "appId": "{{ArmResourceAppId}}" }
                """));

        handler.OnGet(
            url => url.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                {
                  "value": [
                    { "id": "grant-graph", "clientId": "our-sp-obj-1", "resourceId": "graph-sp-obj", "scope": "User.Read" },
                    { "id": "grant-arm",   "clientId": "our-sp-obj-1", "resourceId": "arm-sp-obj",   "scope": "user_impersonation" }
                  ]
                }
                """));

        handler.OnDelete(url => url.AbsolutePath.StartsWith("/v1.0/oauth2PermissionGrants/", StringComparison.Ordinal), _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        var permissions = NewPermissions(handler);
        var required = new[] { GraphRequirement("User.Read", UserReadScopeId) };
        var rejected = new[]
        {
            new RejectedRequirement("plugin-x", PermissionApi.AzureResourceManager, "user_impersonation", "user_impersonation"),
        };

        var result = await permissions.EnsureAsync("tenant-1", "client-1", required, rejected, CancellationToken.None);

        Assert.Equal(0, result.StaleGrantsRemoved);
        Assert.DoesNotContain(handler.Recorded, r => r.Method == HttpMethod.Delete);
        // The ARM grant really was considered for pruning and spared, not
        // skipped because the run never got that far.
        Assert.Contains(handler.Recorded, r => r.Uri.AbsolutePath == "/v1.0/servicePrincipals/arm-sp-obj");
    }

    private static AppRegistrationPermissions NewPermissions(RoutedHttpHandler handler)
        => new(NewGraphClient(handler), NullLogger<AppRegistrationPermissions>.Instance);
}
