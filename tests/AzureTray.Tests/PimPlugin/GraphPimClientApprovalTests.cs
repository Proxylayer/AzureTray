using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM;
using AzureTray.Plugin.PIM.Graph;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The approver side of PIM for directory roles. Three things are load-bearing
// and each is a silent failure if it drifts:
//
//   * the route is BETA — roleAssignmentApprovals is not on v1.0 — and the
//     approval's child collection does not come back unless the GET asks for it
//     with $expand=steps, so a missing expand looks exactly like "no open step";
//   * the child resource is STEPS, not stages (that is the Groups shape);
//   * a step can list several approvers and the first decision closes it for
//     everyone, so a losing PATCH comes back 409. That is an outcome, not a
//     fault: together with "no step still in progress" it must reach the caller
//     as ApprovalAlreadyDecidedException so the watcher can say "somebody else
//     got there first" instead of raising an error the user cannot act on.
//
// The last pair of tests is the guard on that classification: only 409 is the
// race. Any other failing status must keep its own exception type, or the new
// catch silently reports a permissions or outage problem as a decided approval.
public sealed class GraphPimClientApprovalTests
{
    private const string ApprovalUrl =
        "beta/roleManagement/directory/roleAssignmentApprovals/appr-1?$expand=steps";

    private const string StepUrl =
        "beta/roleManagement/directory/roleAssignmentApprovals/appr-1/steps/step-2";

    // ---- the happy path ---------------------------------------------------

    [Fact]
    public async Task ReviewAsync_ReadsTheApprovalWithExpandedSteps_ThenPatchesTheOpenStep()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json(TwoStepApprovalJson));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        Assert.Equal(ApprovalUrl, http.Calls[0].Url, StringComparer.Ordinal);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Equal(StepUrl, patch.Url, StringComparer.Ordinal);
        Assert.DoesNotContain("/stages/", patch.Url, StringComparison.Ordinal);
        Assert.StartsWith("beta/", patch.Url, StringComparison.Ordinal);
    }

    // PascalCase on the wire: "approve" is not a value the service accepts.
    [Fact]
    public async Task ReviewAsync_Approve_SendsThePascalCaseReviewResult()
    {
        var body = await ReviewBodyAsync(ApprovalDecision.Approve);

        Assert.Equal("Approve", body.GetProperty("reviewResult").GetString());
        Assert.Equal("because", body.GetProperty("justification").GetString());
    }

    [Fact]
    public async Task ReviewAsync_Deny_SendsThePascalCaseReviewResult()
    {
        var body = await ReviewBodyAsync(ApprovalDecision.Deny);

        Assert.Equal("Deny", body.GetProperty("reviewResult").GetString());
    }

    // Step status is an open string set and PascalCase on the wire, so picking
    // the open step is a case-insensitive compare.
    [Fact]
    public async Task ReviewAsync_MatchesTheInProgressStepCaseInsensitively()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json("""
                {
                  "id": "appr-1",
                  "steps": [ { "id": "step-9", "status": "inprogress", "reviewResult": "NotReviewed" } ]
                }
                """));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.EndsWith("/steps/step-9", patch.Url, StringComparison.Ordinal);
    }

    // ---- already decided --------------------------------------------------

    // The race caught a poll earlier: by the time the user clicks there is no
    // step left in progress, so nothing is PATCHed at all.
    [Fact]
    public async Task ReviewAsync_NoOpenStep_SurfacesAsApprovalAlreadyDecided_WithoutPatching()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            {
              "id": "appr-1",
              "steps": [
                { "id": "step-1", "status": "Completed", "reviewResult": "Approve" }
              ]
            }
            """));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.Equal("appr-1", ex.ApprovalId);
        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Patch);

        // The client's own wording rides along as the detail, so classifying the
        // failure does not cost the diagnostic the watcher shows the user.
        Assert.Contains("no open step", ex.Message, StringComparison.Ordinal);
    }

    // An approval whose steps were not expanded, or that carries none at all,
    // reads the same as decided rather than dereferencing nothing.
    [Theory]
    [InlineData("""{ "id": "appr-1" }""")]
    [InlineData("""{ "id": "appr-1", "steps": null }""")]
    [InlineData("""{ "id": "appr-1", "steps": [] }""")]
    public async Task ReviewAsync_NoStepsAtAll_SurfacesAsApprovalAlreadyDecided_WithoutPatching(string approvalJson)
    {
        var http = new RecordingPluginHttp(_ => Json(approvalJson));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Patch);
    }

    // A step that is in progress but has no id cannot be addressed, which is the
    // same dead end as no open step.
    [Fact]
    public async Task ReviewAsync_OpenStepWithoutAnId_SurfacesAsApprovalAlreadyDecided()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            { "id": "appr-1", "steps": [ { "status": "InProgress" } ] }
            """));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));
    }

    // The race lost at the PATCH: another approver on the same step decided
    // first, so the step is closed and Graph answers 409.
    [Fact]
    public async Task ReviewAsync_Conflict_SurfacesAsApprovalAlreadyDecided()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Status(HttpStatusCode.Conflict)
            : Json(TwoStepApprovalJson));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.Equal("appr-1", ex.ApprovalId);

        // The service's own body is not thrown away — it hangs off the inner
        // exception for the log.
        var inner = Assert.IsType<HttpRequestException>(ex.InnerException);
        Assert.Equal(HttpStatusCode.Conflict, inner.StatusCode);
    }

    // ---- and nothing else -------------------------------------------------

    // The guard on the catch above: only 409 means "someone beat you to it". A
    // denied permission or a service outage must keep its own type, or the user
    // is told an approval was decided when nothing of the sort happened.
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReviewAsync_NonConflictFailureOnThePatch_StaysAnHttpRequestException(HttpStatusCode code)
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Status(code)
            : Json(TwoStepApprovalJson));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.Equal(code, ex.StatusCode);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task ReviewAsync_NonConflictFailureOnTheRead_StaysAnHttpRequestException(HttpStatusCode code)
    {
        var http = new RecordingPluginHttp(_ => Status(code));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.Equal(code, ex.StatusCode);
    }

    // A 200 that deserializes to nothing is a missing approval, not a decided
    // one — the two read differently to the user and must stay distinct.
    [Fact]
    public async Task ReviewAsync_ApprovalReadsBackAsNull_ThrowsNotFound_NotAlreadyDecided()
    {
        var http = new RecordingPluginHttp(_ => Json("null"));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.IsNotType<ApprovalAlreadyDecidedException>(ex);
    }

    // ---- canned payloads --------------------------------------------------

    private const string TwoStepApprovalJson = """
        {
          "id": "appr-1",
          "steps": [
            { "id": "step-1", "status": "Completed", "reviewResult": "Approve" },
            { "id": "step-2", "status": "InProgress", "reviewResult": "NotReviewed" }
          ]
        }
        """;

    // ---- harness ----------------------------------------------------------

    // Decides the two-step approval and hands back the PATCH body.
    private static async Task<JsonElement> ReviewBodyAsync(ApprovalDecision decision)
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json(TwoStepApprovalJson));
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", decision, "because", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        return JsonDocument.Parse(patch.Body).RootElement;
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphPimClientApprovalTests>.Instance);
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code)
    {
        Content = new StringContent(
            """{ "error": { "code": "Request_Conflict", "message": "already decided" } }""",
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
