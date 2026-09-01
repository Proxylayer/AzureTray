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
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The approver side of PIM for Azure resource roles. Three things are
// load-bearing and each is a silent failure if it drifts:
//
//   * roleAssignmentApprovals is a TENANT-LEVEL collection — the URL carries no
//     scope segment at all, whatever scope the underlying request was made at.
//     A scope prefix happens to route at subscription level and 404s at
//     management-group level, which is the worst possible way to be wrong;
//   * the child resource is STAGES addressed by their NAME (the id is a full
//     ARM resource path and cannot be pasted into the URL), and the status sits
//     one level down under the stage's properties;
//   * a stage can list several approvers and the first decision closes it for
//     everyone, so a losing PATCH comes back 409. That is an outcome, not a
//     fault: together with "no stage still in progress" it must reach the caller
//     as ApprovalAlreadyDecidedException so the watcher can say "somebody else
//     got there first" instead of raising an error the user cannot act on.
//
// The last pair of tests is the guard on that classification: only 409 is the
// race. Any other failing status must keep its own exception type, or the new
// catch silently reports a permissions or outage problem as a decided approval.
public sealed class ArmPimClientApprovalTests
{
    private const string ApprovalUrl =
        "providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1?api-version=2021-01-01-preview";

    private const string StageUrl =
        "providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1/stages/stage-2" +
        "?api-version=2021-01-01-preview";

    // ---- the happy path ---------------------------------------------------

    [Fact]
    public async Task ReviewAsync_ReadsTheTenantLevelApproval_ThenPatchesTheOpenStageByName()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json(TwoStageApprovalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        Assert.Equal(ApprovalUrl, http.Calls[0].Url, StringComparer.Ordinal);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Equal(StageUrl, patch.Url, StringComparer.Ordinal);
        Assert.DoesNotContain("/steps/", patch.Url, StringComparison.Ordinal);
    }

    // No scope segment on either URL, and no leading slash that would make the
    // relative URI resolve away from the ARM base address.
    [Theory]
    [InlineData("/subscriptions/")]
    [InlineData("/providers/Microsoft.Management/managementGroups/")]
    [InlineData("resourceGroups/")]
    public async Task ReviewAsync_CarriesNoScopeSegmentOnEitherCall(string scopeFragment)
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json(TwoStageApprovalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        Assert.All(http.Calls, call =>
        {
            Assert.DoesNotContain(scopeFragment, call.Url, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("providers/Microsoft.Authorization/", call.Url, StringComparison.Ordinal);
        });
    }

    // The stage's id is a full ARM resource path; only its name belongs in the
    // URL, or the PATCH targets a path with another copy of the scope in it.
    [Fact]
    public async Task ReviewAsync_AddressesTheStageByName_NotByItsResourceId()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json(TwoStageApprovalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Contains("/stages/stage-2?", patch.Url, StringComparison.Ordinal);
        Assert.DoesNotContain("/stages//", patch.Url, StringComparison.Ordinal);
    }

    // ARM nests the decision under properties, unlike either Graph client.
    [Fact]
    public async Task ReviewAsync_Approve_SendsTheDecisionNestedUnderProperties()
    {
        var body = await ReviewBodyAsync(ApprovalDecision.Approve);

        var properties = body.GetProperty("properties");
        Assert.Equal("Approve", properties.GetProperty("reviewResult").GetString());
        Assert.Equal("because", properties.GetProperty("justification").GetString());
    }

    [Fact]
    public async Task ReviewAsync_Deny_SendsTheDecisionNestedUnderProperties()
    {
        var body = await ReviewBodyAsync(ApprovalDecision.Deny);

        Assert.Equal("Deny", body.GetProperty("properties").GetProperty("reviewResult").GetString());
    }

    // Stage status is an open string set and PascalCase on the wire, so picking
    // the open stage is a case-insensitive compare.
    [Fact]
    public async Task ReviewAsync_MatchesTheInProgressStageCaseInsensitively()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json("""
                {
                  "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1",
                  "name": "appr-1",
                  "properties": {
                    "stages": [
                      {
                        "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1/stages/stage-9",
                        "name": "stage-9",
                        "properties": { "status": "inprogress" }
                      }
                    ]
                  }
                }
                """));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Contains("/stages/stage-9?", patch.Url, StringComparison.Ordinal);
    }

    // ---- already decided --------------------------------------------------

    // The race caught a poll earlier: by the time the user clicks there is no
    // stage left in progress, so nothing is PATCHed at all.
    [Fact]
    public async Task ReviewAsync_NoOpenStage_SurfacesAsApprovalAlreadyDecided_WithoutPatching()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            {
              "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1",
              "name": "appr-1",
              "properties": {
                "stages": [
                  {
                    "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1/stages/stage-1",
                    "name": "stage-1",
                    "properties": { "status": "Completed", "reviewResult": "Approve" }
                  }
                ]
              }
            }
            """));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.Equal("appr-1", ex.ApprovalId);
        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Patch);

        // The client's own wording rides along as the detail, so classifying the
        // failure does not cost the diagnostic the watcher shows the user.
        Assert.Contains("no open stage", ex.Message, StringComparison.Ordinal);
    }

    // An approval with no stages at all — missing properties, a null collection
    // or an empty one — reads the same as decided rather than dereferencing
    // nothing.
    [Theory]
    [InlineData("""{ "id": "/…/appr-1", "name": "appr-1" }""")]
    [InlineData("""{ "id": "/…/appr-1", "name": "appr-1", "properties": null }""")]
    [InlineData("""{ "id": "/…/appr-1", "name": "appr-1", "properties": { "stages": null } }""")]
    [InlineData("""{ "id": "/…/appr-1", "name": "appr-1", "properties": { "stages": [] } }""")]
    public async Task ReviewAsync_NoStagesAtAll_SurfacesAsApprovalAlreadyDecided_WithoutPatching(string approvalJson)
    {
        var http = new RecordingPluginHttp(_ => Json(approvalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Patch);
    }

    // A stage that is in progress but nameless cannot be addressed, which is the
    // same dead end as no open stage.
    [Fact]
    public async Task ReviewAsync_OpenStageWithoutAName_SurfacesAsApprovalAlreadyDecided()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            {
              "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1",
              "name": "appr-1",
              "properties": {
                "stages": [
                  {
                    "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1/stages/stage-1",
                    "properties": { "status": "InProgress" }
                  }
                ]
              }
            }
            """));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Patch);
    }

    // The race lost at the PATCH: another approver on the same stage decided
    // first, so the stage is closed and ARM answers 409.
    [Fact]
    public async Task ReviewAsync_Conflict_SurfacesAsApprovalAlreadyDecided()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Status(HttpStatusCode.Conflict)
            : Json(TwoStageApprovalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

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
            : Json(TwoStageApprovalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

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
        var client = new ArmPimClient(NewContext(http), "tenant-1");

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
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.IsNotType<ApprovalAlreadyDecidedException>(ex);
    }

    // ---- canned payloads --------------------------------------------------

    private const string TwoStageApprovalJson = """
        {
          "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1",
          "name": "appr-1",
          "properties": {
            "stages": [
              {
                "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1/stages/stage-1",
                "name": "stage-1",
                "properties": { "status": "Completed", "reviewResult": "Approve" }
              },
              {
                "id": "/providers/Microsoft.Authorization/roleAssignmentApprovals/appr-1/stages/stage-2",
                "name": "stage-2",
                "properties": { "status": "InProgress", "assignedToMe": true }
              }
            ]
          }
        }
        """;

    // ---- harness ----------------------------------------------------------

    // Decides the two-stage approval and hands back the PATCH body.
    private static async Task<JsonElement> ReviewBodyAsync(ApprovalDecision decision)
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Ok()
            : Json(TwoStageApprovalJson));
        var client = new ArmPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", decision, "because", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        return JsonDocument.Parse(patch.Body).RootElement;
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<ArmPimClientApprovalTests>.Instance);
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
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
            """{ "error": { "code": "Conflict", "message": "already decided" } }""",
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
