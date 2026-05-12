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
        var required = new[] { GraphRequirement("User.Read", "id-1") };

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
                      "resourceAccess": [{ "id": "id-user-read", "type": "Scope" }]
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
            GraphRequirement("User.Read", "id-user-read"),
            GraphRequirement("RoleManagement.Read.Directory", "id-role-mgmt"),
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
                      "resourceAccess": [{ "id": "id-user-read", "type": "Scope" }]
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
        var required = new[] { GraphRequirement("User.Read", "id-user-read") };

        var result = await permissions.CheckAsync("tenant-1", "client-1", required, CancellationToken.None);

        Assert.True(result.IsFullyConfigured);
        Assert.Empty(result.Missing);
        Assert.Empty(result.NotConsented);
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
                        { "id": "id-user-read",  "type": "Scope" },
                        { "id": "id-role-mgmt",  "type": "Scope" }
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
        var required = new[] { GraphRequirement("User.Read", "id-user-read") };

        var result = await permissions.EnsureAsync("tenant-1", "client-1", required, CancellationToken.None);

        // No new scopes/grants since User.Read was already declared and consented.
        Assert.Empty(result.ScopesAdded);
        Assert.Empty(result.GrantsAdded);
        // But the stale ones got pruned.
        Assert.Equal(1, result.StaleScopesRemoved);
        Assert.Equal(1, result.StaleGrantsRemoved);

        // Verify the RRA PATCH dropped the stale scope.
        var rraPatch = handler.Recorded.Single(r => r.Method == HttpMethod.Patch && r.Uri.AbsolutePath == "/v1.0/applications/app-obj-1");
        Assert.NotNull(rraPatch.Body);
        Assert.Contains("\"id-user-read\"", rraPatch.Body);
        Assert.DoesNotContain("\"id-role-mgmt\"", rraPatch.Body);

        // Verify the grant PATCH replaced the scope string with exactly the required scope.
        var grantPatch = handler.Recorded.Single(r => r.Method == HttpMethod.Patch && r.Uri.AbsolutePath == "/v1.0/oauth2PermissionGrants/grant-1");
        Assert.NotNull(grantPatch.Body);
        Assert.Contains("\"scope\":\"User.Read\"", grantPatch.Body);
        Assert.DoesNotContain("RoleManagement", grantPatch.Body);
    }

    private static PluginPermissionRequirement GraphRequirement(string name, string id)
        => new(PermissionApi.MicrosoftGraph, name, id, name);

    private static AppRegistrationPermissions NewPermissions(RoutedHttpHandler handler)
        => new(NewGraphClient(handler), NullLogger<AppRegistrationPermissions>.Instance);
}
