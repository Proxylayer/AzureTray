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
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The approver side of PIM for Groups. Three things differ from the
// directory-role approvals and each is a silent failure if it drifts:
//
//   * the child resource is STAGES, not steps — a PATCH to /steps/{id} 404s
//     against a group approval, and the stages arrive inline on a GET with no
//     $expand needed;
//   * reviewResult is PascalCase in both directions ("Approve" / "Deny");
//   * a stage can list several approvers and the first decision closes it for
//     everyone, so a losing PATCH comes back 409. That is an outcome, not a
//     fault: it must reach the caller as ApprovalAlreadyDecidedException so the
//     watcher can say "somebody else got there first" instead of raising an
//     error the user cannot act on.
//
// The list side is two steps, because the approval object carries nothing but
// its id and its stages: list what the signed-in user may decide, then read
// each one's underlying request — which shares the approval's id — for the
// requestor, the group and the justification the approver needs to see.
public sealed class GraphGroupPimClientApprovalTests
{
    private const string ApprovalsUrl =
        "v1.0/identityGovernance/privilegedAccess/group/assignmentApprovals/filterByCurrentUser(on='approver')";

    // ---- listing ----------------------------------------------------------

    [Fact]
    public async Task ListPendingApprovalsAsync_ListsViaFilterByCurrentUserOnApprover_ThenReadsEachRequest()
    {
        var http = new RecordingPluginHttp(call =>
            call.Url.Contains("assignmentApprovals", StringComparison.Ordinal)
                ? Json("""{ "value": [ { "id": "req-1" } ] }""")
                : Json(PendingRequestJson("req-1")));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var pending = await client.ListPendingApprovalsAsync(CancellationToken.None);

        Assert.Equal(ApprovalsUrl, http.Calls[0].Url, StringComparer.Ordinal);
        Assert.StartsWith(
            "v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/req-1",
            http.Calls[1].Url,
            StringComparison.Ordinal);
        Assert.Contains("$expand=principal,group", http.Calls[1].Url, StringComparison.Ordinal);

        var request = Assert.Single(pending);
        Assert.Equal("req-1", request.Id);
        Assert.Equal("Alice", request.Principal?.DisplayName);
        Assert.Equal("Contoso SQL Admins", request.Group?.DisplayName);
    }

    // The approver list can include approvals whose request has since been
    // decided or withdrawn. Only a still-pending one is actionable.
    [Fact]
    public async Task ListPendingApprovalsAsync_DropsRequestsThatAreNoLongerPending()
    {
        var http = new RecordingPluginHttp(call =>
            call.Url.Contains("assignmentApprovals", StringComparison.Ordinal)
                ? Json("""{ "value": [ { "id": "req-1" }, { "id": "req-2" } ] }""")
            : call.Url.Contains("req-2", StringComparison.Ordinal)
                ? Json(PendingRequestJson("req-2", status: "Provisioned"))
            : Json(PendingRequestJson("req-1")));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var pending = await client.ListPendingApprovalsAsync(CancellationToken.None);

        var request = Assert.Single(pending);
        Assert.Equal("req-1", request.Id);
    }

    // One unreadable request must not take the whole approver feed down with it.
    [Fact]
    public async Task ListPendingApprovalsAsync_UnreadableRequest_IsSkipped_LeavingTheRestOfTheFeed()
    {
        var http = new RecordingPluginHttp(call =>
            call.Url.Contains("assignmentApprovals", StringComparison.Ordinal)
                ? Json("""{ "value": [ { "id": "req-1" }, { "id": "req-2" } ] }""")
            : call.Url.Contains("req-2", StringComparison.Ordinal)
                ? Status(HttpStatusCode.InternalServerError)
            : Json(PendingRequestJson("req-1")));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var pending = await client.ListPendingApprovalsAsync(CancellationToken.None);

        Assert.Equal("req-1", Assert.Single(pending).Id);
    }

    [Fact]
    public async Task ListPendingApprovalsAsync_NoApprovals_ReadsNoRequests()
    {
        var http = new RecordingPluginHttp(_ => Json("""{ "value": [] }"""));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        Assert.Empty(await client.ListPendingApprovalsAsync(CancellationToken.None));
        Assert.Single(http.Calls);
    }

    // Status is an open string set and PascalCase on the wire, so the
    // still-pending check is case-insensitive rather than an ordinal compare.
    [Theory]
    [InlineData("PendingApproval")]
    [InlineData("pendingapproval")]
    [InlineData("PENDINGAPPROVAL")]
    public async Task ListPendingApprovalsAsync_MatchesThePendingStatusCaseInsensitively(string status)
    {
        var http = new RecordingPluginHttp(call =>
            call.Url.Contains("assignmentApprovals", StringComparison.Ordinal)
                ? Json("""{ "value": [ { "id": "req-1" } ] }""")
                : Json(PendingRequestJson("req-1", status)));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        Assert.Single(await client.ListPendingApprovalsAsync(CancellationToken.None));
    }

    // A request whose group was not expanded still has to name the group in the
    // approval prompt, so the name is resolved separately.
    [Fact]
    public async Task ListPendingApprovalsAsync_UnexpandedGroup_ResolvesTheDisplayNameSeparately()
    {
        var http = new RecordingPluginHttp(call =>
            call.Url.Contains("assignmentApprovals", StringComparison.Ordinal)
                ? Json("""{ "value": [ { "id": "req-1" } ] }""")
            : call.Url.StartsWith("v1.0/groups/", StringComparison.Ordinal)
                ? Json("""{ "id": "group-1", "displayName": "Contoso SQL Admins" }""")
            : Json("""
                {
                  "id": "req-1",
                  "status": "PendingApproval",
                  "action": "adminAssign",
                  "accessId": "member",
                  "groupId": "group-1",
                  "justification": "on call"
                }
                """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var request = Assert.Single(await client.ListPendingApprovalsAsync(CancellationToken.None));

        Assert.Equal("Contoso SQL Admins", request.Group?.DisplayName);
    }

    // ---- deciding ---------------------------------------------------------

    [Fact]
    public async Task ReviewAsync_PatchesTheOpenStage_NotAStep()
    {
        var http = new RecordingPluginHttp(_ => Json(TwoStageApprovalJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.Equal(
            "v1.0/identityGovernance/privilegedAccess/group/assignmentApprovals/appr-1/stages/stage-2",
            patch.Url,
            StringComparer.Ordinal);
        Assert.DoesNotContain("/steps/", patch.Url, StringComparison.Ordinal);
    }

    // PascalCase on the wire in both directions: "approve" is not a value the
    // service accepts.
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

    // The race: another approver in the same stage decided first, so the stage
    // is closed and Graph answers 409.
    [Fact]
    public async Task ReviewAsync_Conflict_SurfacesAsApprovalAlreadyDecided()
    {
        var http = new RecordingPluginHttp(call => call.Method == HttpMethod.Patch
            ? Status(HttpStatusCode.Conflict)
            : Json(TwoStageApprovalJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.Equal("appr-1", ex.ApprovalId);
    }

    // The same race caught a poll earlier: by the time the user clicks there is
    // no stage left in progress, so nothing is PATCHed at all.
    [Fact]
    public async Task ReviewAsync_NoOpenStage_SurfacesAsApprovalAlreadyDecided_WithoutPatching()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            {
              "id": "appr-1",
              "stages": [
                { "id": "stage-1", "status": "Completed", "reviewResult": "Approve" }
              ]
            }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<ApprovalAlreadyDecidedException>(() =>
            client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None));

        Assert.DoesNotContain(http.Calls, c => c.Method == HttpMethod.Patch);
    }

    // Stage status is an open string set too, and the same PascalCase caveat
    // applies to picking which stage is still open.
    [Fact]
    public async Task ReviewAsync_MatchesTheInProgressStageCaseInsensitively()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            {
              "id": "appr-1",
              "stages": [ { "id": "stage-9", "status": "inprogress", "reviewResult": "NotReviewed" } ]
            }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        Assert.EndsWith("/stages/stage-9", patch.Url, StringComparison.Ordinal);
    }

    // reviewedBy arrives as an identity OBJECT. It is deliberately not modelled,
    // and a stray one must not break the read that picks the stage to PATCH.
    [Fact]
    public async Task ReviewAsync_ApprovalCarryingAReviewedByObject_StillDeserializes()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            {
              "id": "appr-1",
              "stages": [
                {
                  "id": "stage-1",
                  "status": "Completed",
                  "reviewResult": "Approve",
                  "reviewedBy": { "id": "prin-9", "displayName": "Bob", "userPrincipalName": "bob@contoso.com" }
                },
                { "id": "stage-2", "status": "InProgress", "reviewResult": "NotReviewed" }
              ]
            }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", ApprovalDecision.Approve, "approved", CancellationToken.None);

        Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
    }

    // ---- canned payloads --------------------------------------------------

    private const string TwoStageApprovalJson = """
        {
          "id": "appr-1",
          "stages": [
            { "id": "stage-1", "status": "Completed", "reviewResult": "Approve" },
            { "id": "stage-2", "status": "InProgress", "reviewResult": "NotReviewed", "assignedToMe": true }
          ]
        }
        """;

    private static string PendingRequestJson(string id, string status = "PendingApproval") => $$"""
        {
          "id": "{{id}}",
          "status": "{{status}}",
          "action": "adminAssign",
          "accessId": "Member",
          "principalId": "prin-9",
          "groupId": "group-1",
          "justification": "on call this week",
          "principal": { "id": "prin-9", "displayName": "Alice", "userPrincipalName": "alice@contoso.com" },
          "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
        }
        """;

    // ---- harness ----------------------------------------------------------

    // Decides the two-stage approval and hands back the PATCH body.
    private static async Task<JsonElement> ReviewBodyAsync(ApprovalDecision decision)
    {
        var http = new RecordingPluginHttp(_ => Json(TwoStageApprovalJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ReviewAsync("appr-1", decision, "because", CancellationToken.None);

        var patch = Assert.Single(http.Calls, c => c.Method == HttpMethod.Patch);
        return JsonDocument.Parse(patch.Body).RootElement;
    }

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphGroupPimClientApprovalTests>.Instance);
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

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
            return _reply(call);
        }
    }
}
