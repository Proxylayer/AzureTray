using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Regression cover for the self-id 409 reconciliation in
// ArmPimClient.PutScheduleRequestAsync (shared by Activate/DeactivateRoleAsync).
//
// ARM PIM write PUTs go to roleAssignmentScheduleRequests/{requestId} where the
// GUID is client-generated. When a per-attempt timeout aborts a slow-but-
// committed PUT and the resilience handler retries it, ARM answers 409 "a role
// assignment request with Id {guid} already exists". Because we own that GUID, a
// 409 naming *our* id is proof the earlier attempt won: we reconcile by GETting
// the committed request. Any other 409 still throws. The retry happens above the
// IPluginHttpClient seam, so this transport only ever sees a single 409 for the
// PUT.
public sealed class ArmPimClientConflictTests
{
    private const string Scope = "/subscriptions/sub-1";
    private const string PrincipalId = "prin-1";
    private const string RoleDefinitionId =
        "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/role-a";

    // A different, fixed GUID that is NOT the one the client generated for the PUT.
    private const string ForeignRequestId = "00000000-0000-0000-0000-0000deadbeef";

    // 1. A 409 whose body names the requestId we PUT is our own committed write:
    //    the follow-up GET to the same URL returns the request and no exception
    //    escapes.
    [Fact]
    public async Task ActivateRoleAsync_SelfIdConflict_ReconcilesViaGet_AndReturnsCommittedRequest()
    {
        var http = new MethodRoutedPluginHttp(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                // Echo the client-generated GUID from the PUT URL into the 409
                // body so the substring match in PutScheduleRequestAsync hits.
                var requestId = ExtractRequestId(request);
                return Conflict(requestId);
            }

            // GET reconciliation of the same roleAssignmentScheduleRequests/{id}.
            return Json("""
                {
                  "id": "/.../req-committed",
                  "name": "req-committed",
                  "properties": { "status": "Provisioned", "roleDefinitionId": "/.../role-a" }
                }
                """);
        });

        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var result = await client.ActivateRoleAsync(
            scope: Scope,
            principalId: PrincipalId,
            roleDefinitionId: RoleDefinitionId,
            linkedRoleEligibilityScheduleId: "elig-1",
            duration: TimeSpan.FromHours(8),
            justification: "incident #42",
            CancellationToken.None);

        Assert.Equal("Provisioned", result.Properties?.Status);
        Assert.Equal(HttpMethod.Put, http.Methods[0]);
        Assert.Equal(HttpMethod.Get, http.Methods[1]);
    }

    // 2. A 409 naming a *different* GUID is a genuine conflict: it must surface as
    //    an HttpRequestException carrying the Conflict status, and no GET is made.
    [Fact]
    public async Task ActivateRoleAsync_ForeignIdConflict_Throws_Conflict()
    {
        var http = new MethodRoutedPluginHttp(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return Conflict(ForeignRequestId);
            }

            throw new InvalidOperationException(
                "A reconciliation GET must not be issued for a foreign-id 409.");
        });

        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.ActivateRoleAsync(
            scope: Scope,
            principalId: PrincipalId,
            roleDefinitionId: RoleDefinitionId,
            linkedRoleEligibilityScheduleId: "elig-1",
            duration: TimeSpan.FromHours(8),
            justification: "incident #42",
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Single(http.Methods);
    }

    // 3. A self-id 409 where the reconciling GET reads back nothing (JSON null)
    //    must NOT be reported as success — the swallow only survives when the
    //    committed request is genuinely readable.
    [Fact]
    public async Task ActivateRoleAsync_SelfIdConflict_ButReconcilingGetReadsNull_Throws()
    {
        var http = new MethodRoutedPluginHttp(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return Conflict(ExtractRequestId(request));
            }

            // 200 but an empty JSON body — ReadFromJsonAsync yields null.
            return Json("null");
        });

        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.ActivateRoleAsync(
            scope: Scope,
            principalId: PrincipalId,
            roleDefinitionId: RoleDefinitionId,
            linkedRoleEligibilityScheduleId: "elig-1",
            duration: TimeSpan.FromHours(8),
            justification: "incident #42",
            CancellationToken.None));
    }

    // The shared helper means the same guarantee holds for deactivation; one case
    // proves the wiring, not the whole matrix again.
    [Fact]
    public async Task DeactivateRoleAsync_SelfIdConflict_ReconcilesViaGet_AndReturnsCommittedRequest()
    {
        var http = new MethodRoutedPluginHttp(request =>
        {
            if (request.Method == HttpMethod.Put)
            {
                return Conflict(ExtractRequestId(request));
            }

            return Json("""
                {
                  "id": "/.../req-committed",
                  "name": "req-committed",
                  "properties": { "status": "Revoked", "roleDefinitionId": "/.../role-a" }
                }
                """);
        });

        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var result = await client.DeactivateRoleAsync(
            scope: Scope,
            principalId: PrincipalId,
            roleDefinitionId: RoleDefinitionId,
            justification: "no longer needed",
            CancellationToken.None);

        Assert.Equal("Revoked", result.Properties?.Status);
        Assert.Equal(HttpMethod.Put, http.Methods[0]);
        Assert.Equal(HttpMethod.Get, http.Methods[1]);
    }

    // ---- harness ----------------------------------------------------------

    // The client PUTs/GETs to roleAssignmentScheduleRequests/{guid}?api-version=…;
    // pull that {guid} back out so a 409 body can echo it.
    private static string ExtractRequestId(HttpRequestMessage request)
    {
        var url = request.RequestUri?.ToString() ?? string.Empty;
        const string marker = "roleAssignmentScheduleRequests/";
        var start = url.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"PUT/GET URL did not target roleAssignmentScheduleRequests: {url}");
        start += marker.Length;
        var end = url.IndexOf('?', start);
        return end < 0 ? url[start..] : url[start..end];
    }

    private static HttpResponseMessage Conflict(string requestId) =>
        new(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                $$"""
                {
                  "error": {
                    "code": "RoleAssignmentRequestExists",
                    "message": "A role assignment request with Id: {{requestId}} already exists."
                  }
                }
                """,
                Encoding.UTF8,
                "application/json"),
        };

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<ArmPimClientConflictTests>.Instance);
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    // Replies from a single function of the request and records each method seen,
    // so a test can branch PUT vs GET and assert the reconciliation GET happened.
    private sealed class MethodRoutedPluginHttp : IPluginHttpClient
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _reply;

        public MethodRoutedPluginHttp(Func<HttpRequestMessage, HttpResponseMessage> reply) { _reply = reply; }

        public List<HttpMethod> Methods { get; } = new();

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            return Task.FromResult(_reply(request));
        }
    }
}
