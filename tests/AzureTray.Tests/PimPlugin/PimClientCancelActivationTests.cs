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
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Withdrawing an activation request that is still awaiting approval. All three
// clients speak the same shape — POST to the request's /cancel action with no
// body, where the only meaningful reply is the status code — but each builds
// the URL from its own root, and two things are load-bearing per client:
//
//   * the exact cancel URL (Graph is v1.0, ARM carries the request's scope
//     prefix plus its api-version, Groups hangs off the privilegedAccess root);
//   * a refusal (the service answers 400 once the request is already decided)
//     surfaces as the client's usual HttpRequestException carrying the status,
//     exactly like every other call — the watcher's catch depends on that.
public sealed class PimClientCancelActivationTests
{
    // ---- Graph (Entra ID directory roles) -----------------------------------

    [Fact]
    public async Task Graph_CancelActivationAsync_PostsAnEmptyBodyToTheCancelAction()
    {
        var http = new RecordingPluginHttp(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.CancelActivationAsync("req-1", CancellationToken.None);

        var call = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(
            "v1.0/roleManagement/directory/roleAssignmentScheduleRequests/req-1/cancel",
            call.Url,
            StringComparer.Ordinal);
        Assert.Equal(string.Empty, call.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Graph_CancelActivationAsync_NonSuccess_SurfacesAsHttpRequestException(HttpStatusCode code)
    {
        var http = new RecordingPluginHttp(_ => Status(code));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CancelActivationAsync("req-1", CancellationToken.None));

        Assert.Equal(code, ex.StatusCode);
    }

    // ---- ARM (Azure RBAC) ---------------------------------------------------

    [Fact]
    public async Task Arm_CancelActivationAsync_PostsAnEmptyBodyToTheScopedCancelAction()
    {
        var http = new RecordingPluginHttp(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await client.CancelActivationAsync("/subscriptions/sub-1", "req-arm-1", CancellationToken.None);

        var call = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(
            "subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-arm-1/cancel"
                + "?api-version=2020-10-01",
            call.Url,
            StringComparer.Ordinal);
        Assert.Equal(string.Empty, call.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Arm_CancelActivationAsync_NonSuccess_SurfacesAsHttpRequestException(HttpStatusCode code)
    {
        var http = new RecordingPluginHttp(_ => Status(code));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CancelActivationAsync("/subscriptions/sub-1", "req-arm-1", CancellationToken.None));

        Assert.Equal(code, ex.StatusCode);
    }

    // ---- Graph (PIM for Groups) ---------------------------------------------

    [Fact]
    public async Task Group_CancelActivationAsync_PostsAnEmptyBodyToTheCancelAction()
    {
        var http = new RecordingPluginHttp(_ => new HttpResponseMessage(HttpStatusCode.NoContent));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.CancelActivationAsync("req-grp-1", CancellationToken.None);

        var call = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(
            "v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/req-grp-1/cancel",
            call.Url,
            StringComparer.Ordinal);
        Assert.Equal(string.Empty, call.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Group_CancelActivationAsync_NonSuccess_SurfacesAsHttpRequestException(HttpStatusCode code)
    {
        var http = new RecordingPluginHttp(_ => Status(code));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CancelActivationAsync("req-grp-1", CancellationToken.None));

        Assert.Equal(code, ex.StatusCode);
    }

    // ---- harness ----------------------------------------------------------

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<PimClientCancelActivationTests>.Instance);
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code)
    {
        Content = new StringContent(
            """{ "error": { "code": "RoleAssignmentRequestCannotBeCanceled", "message": "already decided" } }""",
            Encoding.UTF8,
            "application/json"),
    };

    private sealed record Call(HttpMethod Method, string Url, string Body);

    // Records the method, URL and serialized body of every call, and replies
    // from a single function of the call just recorded.
    private sealed class RecordingPluginHttp : IPluginHttpClient
    {
        private readonly Func<Call, HttpResponseMessage> _reply;

        public RecordingPluginHttp(Func<Call, HttpResponseMessage> reply) { _reply = reply; }

        public List<Call> Calls { get; } = new();

        public async Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The PIM clients build relative URIs, so ToString() is the only safe
            // accessor (AbsoluteUri throws on a relative Uri).
            var call = new Call(
                request.Method,
                Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty),
                request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            Calls.Add(call);

            var response = _reply(call);
            response.RequestMessage = request;
            return response;
        }
    }
}
