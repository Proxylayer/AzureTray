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
using AzureTray.Plugin.PIM.Groups;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The two PIM for Groups list reads, over a stub HTTP handler. What is pinned
// here is the wire contract, because every part of it fails silently rather
// than loudly:
//
//   * both lists go through filterByCurrentUser(on='principal') — the plain
//     list form makes $filter mandatory and would need the signed-in user's
//     object id, which this client otherwise never has to ask for;
//   * $expand=group is a probe, not an assumption: a tenant can answer 4xx for
//     the expanded form while the bare form succeeds, and the fallback must
//     still produce a row for every eligibility — a group whose name cannot be
//     resolved degrades to its bare id, it does not vanish from the menu;
//   * endDateTime on an assignment instance is FLAT, not nested under
//     scheduleInfo.expiration the way the request resources spell it, and a
//     null one means permanent — reading the nested one, or treating null as
//     expired, would misreport access the user actually holds;
//   * the schema documents camelCase enum values but live payloads answer
//     PascalCase ("Member", "Direct", "Assigned"), so a case-sensitive read
//     silently loses them.
public sealed class GraphGroupPimClientTests
{
    private const string EligibleUrl =
        "v1.0/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances/filterByCurrentUser(on='principal')";

    private const string ActiveUrl =
        "v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleInstances/filterByCurrentUser(on='principal')";

    // ---- eligible list ----------------------------------------------------

    [Fact]
    public async Task ListEligibleGroupsAsync_CallsFilterByCurrentUserOnPrincipal_ExpandingTheGroup()
    {
        var http = new RecordingPluginHttp(_ => Json(EligiblePage));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var rows = await client.ListEligibleGroupsAsync(CancellationToken.None);

        var url = Assert.Single(http.Urls);
        Assert.StartsWith(EligibleUrl, url, StringComparison.Ordinal);
        Assert.Contains("$expand=group", url, StringComparison.Ordinal);

        var row = Assert.Single(rows);
        Assert.Equal("group-1", row.GroupId);
        Assert.Equal("Contoso SQL Admins", row.Group?.DisplayName);
    }

    // The fallback: Graph rejects the expanded form, so the read is retried
    // bare and the display name comes from the groups endpoint instead.
    [Fact]
    public async Task ListEligibleGroupsAsync_ExpandRejected_RetriesWithoutIt_AndResolvesTheNameFromTheGroupsEndpoint()
    {
        var http = new RecordingPluginHttp(url =>
            url.Contains("$expand=group", StringComparison.Ordinal) ? Status(HttpStatusCode.BadRequest)
            : url.StartsWith("v1.0/groups/", StringComparison.Ordinal)
                ? Json("""{ "id": "group-1", "displayName": "Contoso SQL Admins" }""")
            : Json(EligiblePageWithoutGroup));

        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var rows = await client.ListEligibleGroupsAsync(CancellationToken.None);

        Assert.Equal(3, http.Urls.Count);
        Assert.Contains("$expand=group", http.Urls[0], StringComparison.Ordinal);
        Assert.DoesNotContain("$expand", http.Urls[1], StringComparison.Ordinal);
        Assert.Equal("v1.0/groups/group-1?$select=id,displayName", http.Urls[2], StringComparer.Ordinal);

        var row = Assert.Single(rows);
        Assert.Equal("Contoso SQL Admins", row.Group?.DisplayName);
    }

    // The row must survive a name that cannot be read at all (a deleted group,
    // or one the signed-in user cannot see): a row the user can still activate
    // beats a row that silently disappeared over a display string.
    [Fact]
    public async Task ListEligibleGroupsAsync_UnresolvableName_StillYieldsTheRow_FallingBackToTheGroupId()
    {
        var http = new RecordingPluginHttp(url =>
            url.Contains("$expand=group", StringComparison.Ordinal) ? Status(HttpStatusCode.BadRequest)
            : url.StartsWith("v1.0/groups/", StringComparison.Ordinal) ? Status(HttpStatusCode.NotFound)
            : Json(EligiblePageWithoutGroup));

        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var row = Assert.Single(await client.ListEligibleGroupsAsync(CancellationToken.None));

        Assert.Equal("group-1", row.GroupId);
        Assert.Equal("group-1", row.Group?.DisplayName);
    }

    // One rejection turns the expansion off for the life of the client, so a
    // tenant that will not expand pays the probe once rather than every poll.
    [Fact]
    public async Task ListEligibleGroupsAsync_ExpandRejectedOnce_StaysOffForTheRestOfTheSession()
    {
        var http = new RecordingPluginHttp(url =>
            url.Contains("$expand=group", StringComparison.Ordinal) ? Status(HttpStatusCode.Forbidden)
            : url.StartsWith("v1.0/groups/", StringComparison.Ordinal)
                ? Json("""{ "id": "group-1", "displayName": "Contoso SQL Admins" }""")
            : Json(EligiblePageWithoutGroup));

        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ListEligibleGroupsAsync(CancellationToken.None);
        var firstCallUrls = http.Urls.Count;
        await client.ListEligibleGroupsAsync(CancellationToken.None);

        // Second poll goes straight to the bare form, and the name is served
        // from the session cache rather than re-read.
        var secondCall = http.Urls.Skip(firstCallUrls).ToList();
        var only = Assert.Single(secondCall);
        Assert.DoesNotContain("$expand", only, StringComparison.Ordinal);
    }

    // When the bare form fails too, the expansion was never proven to be the
    // problem (a missing scope fails either way), so the probe is put back and
    // the next poll tries the expanded form again.
    [Fact]
    public async Task ListEligibleGroupsAsync_BothFormsFail_Throws_AndLeavesTheExpandProbeOn()
    {
        var failing = true;
        var http = new RecordingPluginHttp(url =>
            failing
                ? Status(url.Contains("$expand=group", StringComparison.Ordinal)
                    ? HttpStatusCode.BadRequest
                    : HttpStatusCode.InternalServerError)
                : Json(EligiblePage));

        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<HttpRequestException>(
            () => client.ListEligibleGroupsAsync(CancellationToken.None));

        failing = false;
        await client.ListEligibleGroupsAsync(CancellationToken.None);

        Assert.Equal(3, http.Urls.Count);
        Assert.Contains("$expand=group", http.Urls[2], StringComparison.Ordinal);
    }

    // Live payloads capitalize what the schema documents in camelCase, and the
    // property names themselves are not guaranteed either. Both are read
    // case-insensitively; the values are carried through verbatim.
    [Fact]
    public async Task ListEligibleGroupsAsync_PascalCasePayload_Deserializes()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            { "value": [ {
                "Id": "elig-1",
                "PrincipalId": "prin-1",
                "AccessId": "Member",
                "GroupId": "group-1",
                "MemberType": "Direct",
                "Group": { "Id": "group-1", "DisplayName": "Contoso SQL Admins" }
            } ] }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var row = Assert.Single(await client.ListEligibleGroupsAsync(CancellationToken.None));

        Assert.Equal("Member", row.AccessId);
        Assert.Equal("Direct", row.MemberType);
        Assert.Equal("group-1", row.GroupId);
        // The name arrived expanded, so nothing had to be resolved separately.
        Assert.Equal("Contoso SQL Admins", row.Group?.DisplayName);
        Assert.Single(http.Urls);
    }

    [Fact]
    public async Task ListEligibleGroupsAsync_FollowsODataNextLink()
    {
        var http = new RecordingPluginHttp(url => url.Contains("$skiptoken", StringComparison.Ordinal)
            ? Json("""
                { "value": [ {
                    "id": "elig-2", "principalId": "prin-1", "accessId": "owner", "groupId": "group-2",
                    "group": { "id": "group-2", "displayName": "Contoso Net Admins" }
                } ] }
                """)
            : Json("""
                {
                  "value": [ {
                    "id": "elig-1", "principalId": "prin-1", "accessId": "member", "groupId": "group-1",
                    "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
                  } ],
                  "@odata.nextLink": "https://graph.microsoft.com/v1.0/identityGovernance/privilegedAccess/group/eligibilityScheduleInstances?$skiptoken=page2"
                }
                """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var rows = await client.ListEligibleGroupsAsync(CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.GroupId == "group-2" && r.AccessId == "owner");
    }

    // ---- active list ------------------------------------------------------

    [Fact]
    public async Task ListActiveGroupAssignmentsAsync_CallsFilterByCurrentUserOnPrincipal_WithoutAnExpand()
    {
        var http = new RecordingPluginHttp(_ => Json(EmptyPage));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ListActiveGroupAssignmentsAsync(CancellationToken.None);

        var url = Assert.Single(http.Urls);
        Assert.Equal(ActiveUrl, url, StringComparer.Ordinal);
        Assert.DoesNotContain("$expand", url, StringComparison.Ordinal);
    }

    // The trap: the request resources nest the end time under
    // scheduleInfo.expiration, the instance resources carry it flat. A payload
    // containing both must be read from the flat one.
    [Fact]
    public async Task ListActiveGroupAssignmentsAsync_ReadsEndDateTimeFlat_NotFromScheduleInfo()
    {
        var flat = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

        var http = new RecordingPluginHttp(_ => Json($$"""
            { "value": [ {
                "id": "inst-1",
                "principalId": "prin-1",
                "accessId": "member",
                "groupId": "group-1",
                "assignmentType": "Activated",
                "startDateTime": "2026-03-01T08:00:00Z",
                "endDateTime": "{{flat:O}}",
                "scheduleInfo": {
                  "expiration": { "type": "AfterDuration", "endDateTime": "2027-01-01T00:00:00Z" }
                }
            } ] }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var row = Assert.Single(await client.ListActiveGroupAssignmentsAsync(CancellationToken.None));

        Assert.Equal(flat, row.EndDateTime);
    }

    // A permanent (standing) assignment has no end time — and may have no start
    // time either. Null is "permanent", never "expired" or "unknown".
    [Fact]
    public async Task ListActiveGroupAssignmentsAsync_NullEndDateTime_MeansPermanent()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            { "value": [ {
                "id": "inst-1",
                "principalId": "prin-1",
                "accessId": "Owner",
                "groupId": "group-1",
                "memberType": "Direct",
                "assignmentType": "Assigned",
                "startDateTime": null,
                "endDateTime": null
            } ] }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var row = Assert.Single(await client.ListActiveGroupAssignmentsAsync(CancellationToken.None));

        Assert.Null(row.EndDateTime);
        Assert.Null(row.StartDateTime);
        // PascalCase on the wire, carried through as sent.
        Assert.Equal("Owner", row.AccessId);
        Assert.Equal("Assigned", row.AssignmentType);
    }

    // ---- status poll ------------------------------------------------------

    // A group request is addressed by its own id alone — no group segment in
    // the URL — which is why PendingActivationRequest needs nothing extra to
    // poll one.
    [Fact]
    public async Task GetActivationStatusAsync_ReadsTheRequestById()
    {
        var http = new RecordingPluginHttp(_ => Json("""{ "id": "req-1", "status": "PendingApproval" }"""));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var status = await client.GetActivationStatusAsync("req-1", CancellationToken.None);

        Assert.Equal("PendingApproval", status);
        Assert.Equal(
            "v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleRequests/req-1",
            Assert.Single(http.Urls),
            StringComparer.Ordinal);
    }

    // ---- canned payloads --------------------------------------------------

    private const string EmptyPage = """{ "value": [] }""";

    private const string EligiblePage = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "accessId": "member",
            "groupId": "group-1",
            "memberType": "Direct",
            "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
        } ] }
        """;

    private const string EligiblePageWithoutGroup = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "accessId": "member",
            "groupId": "group-1",
            "memberType": "Direct"
        } ] }
        """;

    // ---- harness ----------------------------------------------------------

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphGroupPimClientTests>.Instance);
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
            """{ "error": { "code": "Request_BadRequest", "message": "denied" } }""",
            Encoding.UTF8,
            "application/json"),
    };

    // Records every request URL (unescaped, so the $expand/$select text can be
    // asserted as written) and replies from a single function of that URL.
    private sealed class RecordingPluginHttp : IPluginHttpClient
    {
        private readonly Func<string, HttpResponseMessage> _reply;

        public RecordingPluginHttp(Func<string, HttpResponseMessage> reply) { _reply = reply; }

        public List<string> Urls { get; } = new();

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The PIM clients build relative URIs, so ToString() is the only safe
            // accessor (AbsoluteUri throws on a relative Uri).
            var url = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
            Urls.Add(url);
            return Task.FromResult(_reply(url));
        }
    }
}
