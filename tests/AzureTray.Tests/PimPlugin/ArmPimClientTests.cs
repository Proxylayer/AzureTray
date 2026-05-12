using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

public sealed class ArmPimClientTests
{
    [Fact]
    public async Task ListSubscriptionsAsync_DeserializesAndFollowsScope()
    {
        var routes = new RoutedPluginHttp();
        routes.OnGet("subscriptions?api-version=2022-12-01", _ => Json("""
            { "value": [
                { "id": "/subscriptions/sub-1", "subscriptionId": "sub-1", "displayName": "Dev", "state": "Enabled" },
                { "id": "/subscriptions/sub-2", "subscriptionId": "sub-2", "displayName": "Prod", "state": "Enabled" }
            ] }
            """));

        var client = new ArmPimClient(NewContext(routes));

        var subs = await client.ListSubscriptionsAsync("tenant-1", CancellationToken.None);

        Assert.Equal(2, subs.Count);
        Assert.Equal("sub-1", subs[0].SubscriptionId);
        Assert.Equal("Prod", subs[1].DisplayName);
    }

    [Fact]
    public async Task ListPendingApprovalsAsync_QueriesEachScope_AndConcatenates()
    {
        var routes = new RoutedPluginHttp();
        routes.OnGet(r => r.RequestUri!.PathAndQuery.StartsWith("/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests", StringComparison.Ordinal),
            _ => Json("""
                { "value": [{
                    "id": "/.../req-1",
                    "name": "req-1",
                    "properties": { "status": "PendingApproval", "approvalId": "approval-1" }
                }] }
                """));
        routes.OnGet(r => r.RequestUri!.PathAndQuery.StartsWith("/subscriptions/sub-2/providers/Microsoft.Authorization/roleAssignmentScheduleRequests", StringComparison.Ordinal),
            _ => Json("""
                { "value": [{
                    "id": "/.../req-2",
                    "name": "req-2",
                    "properties": { "status": "PendingApproval", "approvalId": "approval-2" }
                }] }
                """));

        var client = new ArmPimClient(NewContext(routes));

        var pending = await client.ListPendingApprovalsAsync(
            "tenant-1",
            new[] { "/subscriptions/sub-1", "/subscriptions/sub-2" },
            CancellationToken.None);

        Assert.Equal(2, pending.Count);
        Assert.Contains(pending, r => r.Properties?.ApprovalId == "approval-1");
        Assert.Contains(pending, r => r.Properties?.ApprovalId == "approval-2");
    }

    [Fact]
    public async Task ActivateRoleAsync_PutsExpectedBodyShape_AndReturnsRequest()
    {
        HttpMethod? capturedMethod = null;
        Uri? capturedUri = null;
        string? capturedBody = null;
        var routes = new RoutedPluginHttp();
        routes.On(HttpMethod.Put, _ => true, req =>
        {
            capturedMethod = req.Method;
            capturedUri = req.RequestUri;
            capturedBody = req.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
            return Json("""
                {
                  "id": "/.../req-x",
                  "name": "req-x",
                  "properties": {
                    "status": "Provisioned",
                    "roleDefinitionId": "/.../role-a"
                  }
                }
                """);
        });

        var client = new ArmPimClient(NewContext(routes));

        var result = await client.ActivateRoleAsync(
            tenantId: "tenant-1",
            scope: "/subscriptions/sub-1",
            principalId: "prin-1",
            roleDefinitionId: "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/role-a",
            linkedRoleEligibilityScheduleId: "elig-1",
            duration: TimeSpan.FromHours(8),
            justification: "incident #42",
            CancellationToken.None);

        Assert.Equal(HttpMethod.Put, capturedMethod);
        Assert.NotNull(capturedUri);
        Assert.StartsWith("/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/",
            capturedUri!.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("api-version=2020-10-01", capturedUri.Query, StringComparison.Ordinal);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"requestType\":\"SelfActivate\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"justification\":\"incident #42\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"duration\":\"PT8H\"", capturedBody, StringComparison.Ordinal);

        Assert.Equal("Provisioned", result.Properties?.Status);
    }

    [Fact]
    public async Task ReviewAsync_FindsOpenStage_AndPatchesWithDecision()
    {
        string? capturedBody = null;
        var routes = new RoutedPluginHttp();
        routes.OnGet(
            r => r.RequestUri!.AbsolutePath.EndsWith("/roleAssignmentApprovals/approval-1", StringComparison.Ordinal),
            _ => Json("""
                {
                  "id": "approval-1",
                  "name": "approval-1",
                  "properties": {
                    "stages": [
                      { "id": "/.../stages/stage-closed", "name": "stage-closed", "properties": { "status": "Completed" } },
                      { "id": "/.../stages/stage-open",   "name": "stage-open",   "properties": { "status": "InProgress" } }
                    ]
                  }
                }
                """));
        routes.On(HttpMethod.Patch,
            r => r.RequestUri!.AbsolutePath.Contains("/stages/stage-open", StringComparison.Ordinal),
            req =>
            {
                capturedBody = req.Content?.ReadAsStringAsync(CancellationToken.None).GetAwaiter().GetResult();
                return new HttpResponseMessage(HttpStatusCode.OK);
            });

        var client = new ArmPimClient(NewContext(routes));

        await client.ReviewAsync(
            "tenant-1",
            "/subscriptions/sub-1",
            "approval-1",
            ApprovalDecision.Deny,
            "wrong scope",
            CancellationToken.None);

        Assert.NotNull(capturedBody);
        Assert.Contains("\"reviewResult\":\"Deny\"", capturedBody, StringComparison.Ordinal);
        Assert.Contains("\"justification\":\"wrong scope\"", capturedBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetActivationStatusAsync_ReadsStatusField()
    {
        var routes = new RoutedPluginHttp();
        routes.OnGet(_ => true, _ => Json("""
            {
              "id": "/.../req-1",
              "name": "req-1",
              "properties": { "status": "Granted" }
            }
            """));

        var client = new ArmPimClient(NewContext(routes));

        var status = await client.GetActivationStatusAsync(
            "tenant-1",
            "/subscriptions/sub-1",
            "req-1",
            CancellationToken.None);

        Assert.Equal("Granted", status);
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Http.Returns(http);
        ctx.Logger.Returns(NullLogger<ArmPimClientTests>.Instance);
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    // IPluginHttpClient adapter that matches routes by HttpMethod + predicate.
    private sealed class RoutedPluginHttp : IPluginHttpClient
    {
        private static readonly Uri BaseUri = new("https://management.azure.com/");
        private readonly List<(HttpMethod Method, Predicate<HttpRequestMessage> Match, Func<HttpRequestMessage, HttpResponseMessage> Reply)> _routes = new();

        public void On(HttpMethod method, Predicate<HttpRequestMessage> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => _routes.Add((method, match, reply));

        public void OnGet(string urlContains, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => _routes.Add((HttpMethod.Get,
                r => r.RequestUri!.AbsoluteUri.Replace("%20", " ", StringComparison.Ordinal)
                       .Contains(urlContains, StringComparison.Ordinal),
                reply));

        public void OnGet(Predicate<HttpRequestMessage> match, Func<HttpRequestMessage, HttpResponseMessage> reply)
            => _routes.Add((HttpMethod.Get, match, reply));

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string tenantId, string scope,
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is { IsAbsoluteUri: false })
            {
                request.RequestUri = new Uri(BaseUri, request.RequestUri);
            }

            foreach (var (method, match, reply) in _routes)
            {
                if (request.Method == method && match(request))
                {
                    return Task.FromResult(reply(request));
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"No route matched {request.Method} {request.RequestUri}", Encoding.UTF8, "text/plain"),
            });
        }
    }
}
