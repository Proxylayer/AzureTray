using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using AzureTray.AppRegistration;
using AzureTray.Plugin.Contracts;
using Xunit;
using static AzureTray.Tests.AppRegistration.AppRegistrationTestFixtures;

namespace AzureTray.Tests.AppRegistration;

public sealed class AppRegistrationProvisioningTests
{
    [Fact]
    public async Task CreateAsync_PostsApplication_ServicePrincipal_Patches_Broker_And_Consents()
    {
        var handler = new RoutedHttpHandler();

        // POST /applications — Graph returns the new app
        handler.OnPost(
            u => u.AbsolutePath == "/v1.0/applications",
            _ => Json("""
                { "id": "new-app-obj-id", "appId": "new-app-client-id", "displayName": "AzureTray" }
                """));

        // POST /servicePrincipals — Graph returns the new SP
        handler.OnPost(
            u => u.AbsolutePath == "/v1.0/servicePrincipals",
            _ => Json("""
                { "id": "new-sp-obj-id", "appId": "new-app-client-id" }
                """));

        // PATCH /applications/{id} for the broker URI — empty 204
        handler.OnPatch(
            u => u.AbsolutePath == "/v1.0/applications/new-app-obj-id",
            _ => new HttpResponseMessage(HttpStatusCode.NoContent));

        // Resource SP lookup for Graph (for the consent grant)
        handler.OnGet(
            u => u.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(u.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }] }
                """));

        // POST /oauth2PermissionGrants — Graph returns the new grant
        handler.OnPost(
            u => u.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                { "id": "new-grant-id", "clientId": "new-sp-obj-id", "resourceId": "graph-sp-obj", "scope": "User.Read" }
                """));

        var provisioning = NewProvisioning(handler);
        var required = new[] { GraphRequirement("User.Read", "id-user-read") };

        var result = await provisioning.CreateAsync("tenant-1", "AzureTray", required, CancellationToken.None);

        Assert.Equal("new-app-client-id", result.App.AppId);
        Assert.Equal("new-app-obj-id", result.App.ObjectId);
        Assert.Equal("AzureTray", result.App.DisplayName);
        Assert.Equal(1, result.ScopesGranted);
        Assert.True(result.BrokerRedirectUriAdded);

        // Verify the application POST body contained signInAudience + RRA.
        var appPost = handler.Recorded.Single(r => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/v1.0/applications");
        Assert.NotNull(appPost.Body);
        Assert.Contains("\"signInAudience\":\"AzureADMyOrg\"", appPost.Body);
        Assert.Contains("\"id-user-read\"", appPost.Body);

        // Verify the broker URI patch carried the expected URI.
        var patch = handler.Recorded.Single(r => r.Method == HttpMethod.Patch);
        Assert.NotNull(patch.Body);
        Assert.Contains("ms-appx-web://microsoft.aad.brokerplugin/new-app-client-id", patch.Body);
    }

    [Fact]
    public async Task CreateAsync_ReportsBrokerNotAdded_WhenPatchFails()
    {
        var handler = new RoutedHttpHandler();

        handler.OnPost(
            u => u.AbsolutePath == "/v1.0/applications",
            _ => Json("""
                { "id": "new-app-obj-id", "appId": "new-app-client-id", "displayName": "AzureTray" }
                """));

        handler.OnPost(
            u => u.AbsolutePath == "/v1.0/servicePrincipals",
            _ => Json("""
                { "id": "new-sp-obj-id", "appId": "new-app-client-id" }
                """));

        // PATCH /applications fails — server returns 403.
        handler.OnPatch(
            u => u.AbsolutePath == "/v1.0/applications/new-app-obj-id",
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("Insufficient privileges") });

        handler.OnGet(
            u => u.AbsolutePath == "/v1.0/servicePrincipals" && Uri.UnescapeDataString(u.Query).Contains($"appId eq '{GraphResourceAppId}'"),
            _ => Json($$"""
                { "value": [{ "id": "graph-sp-obj", "appId": "{{GraphResourceAppId}}" }] }
                """));

        handler.OnPost(
            u => u.AbsolutePath == "/v1.0/oauth2PermissionGrants",
            _ => Json("""
                { "id": "new-grant-id" }
                """));

        var provisioning = NewProvisioning(handler);
        var required = new[] { GraphRequirement("User.Read", "id-user-read") };

        var result = await provisioning.CreateAsync("tenant-1", "AzureTray", required, CancellationToken.None);

        Assert.Equal("new-app-client-id", result.App.AppId);
        Assert.False(result.BrokerRedirectUriAdded);
        // Consent still proceeded since the broker URI failure is best-effort.
        Assert.Equal(1, result.ScopesGranted);
    }

    private static PluginPermissionRequirement GraphRequirement(string name, string id)
        => new(PermissionApi.MicrosoftGraph, name, id, name);

    private static AppRegistrationProvisioning NewProvisioning(RoutedHttpHandler handler)
        => new(NewGraphClient(handler), NullLogger<AppRegistrationProvisioning>.Instance);
}
