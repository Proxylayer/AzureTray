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
using AzureTray.Plugin.PIM.Groups;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Group self-activation and self-deactivation request bodies. The body is the
// whole contract here — Graph answers 400 with a generic parse message when it
// is wrong — so these tests pin it property by property:
//
//   * startDateTime is OMITTED, never sent as "now": the moment is already in
//     the past by the time Graph evaluates the request, and a past
//     startDateTime is rejected;
//   * the expiration is afterDuration + an ISO-8601 duration, not an
//     endDateTime;
//   * deactivation carries no scheduleInfo at all — but whether that is legal
//     is undocumented, so a 400 is retried once with the most degenerate
//     schedule that can mean "now, for no time at all" (PT0S). Anything other
//     than a 400 is a real failure and must NOT be retried, or a permission
//     error turns into two identical rejected requests per click.
public sealed class GraphGroupPimClientActivationTests
{
    private const string RequestsUrl =
        "v1.0/identityGovernance/privilegedAccess/group/assignmentScheduleRequests";

    private const string CreatedRequestJson = """
        {
          "id": "req-1",
          "status": "PendingApproval",
          "action": "SelfActivate",
          "accessId": "Member",
          "principalId": "prin-1",
          "groupId": "group-1",
          "scheduleInfo": { "expiration": { "type": "AfterDuration", "duration": "PT4H" } }
        }
        """;

    // ---- activation -------------------------------------------------------

    [Fact]
    public async Task ActivateAsync_PostsSelfActivate_WithTheGroupAccessAndAnAfterDurationExpiration()
    {
        var http = new RecordingPluginHttp(_ => Json(CreatedRequestJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ActivateAsync(
            "prin-1", "group-1", "member", TimeSpan.FromHours(4), "incident #42", CancellationToken.None);

        var call = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(RequestsUrl, call.Url, StringComparer.Ordinal);

        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("selfActivate", body.GetProperty("action").GetString());
        Assert.Equal("member", body.GetProperty("accessId").GetString());
        Assert.Equal("group-1", body.GetProperty("groupId").GetString());
        Assert.Equal("prin-1", body.GetProperty("principalId").GetString());
        Assert.Equal("incident #42", body.GetProperty("justification").GetString());

        var expiration = body.GetProperty("scheduleInfo").GetProperty("expiration");
        Assert.Equal("afterDuration", expiration.GetProperty("type").GetString());
        Assert.Equal("PT4H", expiration.GetProperty("duration").GetString());
    }

    // Sending DateTimeOffset.UtcNow is racy: by the time Graph evaluates the
    // request the moment has passed, and a past startDateTime is rejected.
    // Omitting the property is what means "start now".
    [Fact]
    public async Task ActivateAsync_OmitsStartDateTime()
    {
        var http = new RecordingPluginHttp(_ => Json(CreatedRequestJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ActivateAsync(
            "prin-1", "group-1", "member", TimeSpan.FromHours(1), "why", CancellationToken.None);

        var call = Assert.Single(http.Calls);
        Assert.DoesNotContain("startDateTime", call.Body, StringComparison.Ordinal);
        Assert.False(JsonDocument.Parse(call.Body).RootElement
            .GetProperty("scheduleInfo").TryGetProperty("startDateTime", out _));
    }

    // A wire value read back from a list ("Member" / "Owner", capitalized by
    // the service) must be normalized before it goes out again.
    [Theory]
    [InlineData("Owner", "owner")]
    [InlineData("MEMBER", "member")]
    [InlineData(null, "member")]
    public async Task ActivateAsync_NormalizesTheAccessIdItSends(string? accessId, string expected)
    {
        var http = new RecordingPluginHttp(_ => Json(CreatedRequestJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ActivateAsync(
            "prin-1", "group-1", accessId!, TimeSpan.FromHours(1), "why", CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(http.Calls).Body).RootElement;
        Assert.Equal(expected, body.GetProperty("accessId").GetString());
    }

    [Theory]
    [InlineData(30, "PT30M")]
    [InlineData(60, "PT1H")]
    [InlineData(90, "PT1H30M")]
    [InlineData(480, "PT8H")]
    public async Task ActivateAsync_FormatsTheDurationAsIso8601(int minutes, string expected)
    {
        var http = new RecordingPluginHttp(_ => Json(CreatedRequestJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.ActivateAsync(
            "prin-1", "group-1", "member", TimeSpan.FromMinutes(minutes), "why", CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(http.Calls).Body).RootElement;
        Assert.Equal(
            expected,
            body.GetProperty("scheduleInfo").GetProperty("expiration").GetProperty("duration").GetString());
    }

    // PascalCase comes back on the response too, and the reader must keep it
    // rather than losing the status the caller decides on.
    [Fact]
    public async Task ActivateAsync_ReadsThePascalCaseResponse()
    {
        var http = new RecordingPluginHttp(_ => Json(CreatedRequestJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var created = await client.ActivateAsync(
            "prin-1", "group-1", "member", TimeSpan.FromHours(4), "why", CancellationToken.None);

        Assert.Equal("req-1", created.Id);
        Assert.Equal("PendingApproval", created.Status);
        Assert.Equal("SelfActivate", created.Action);
        Assert.Equal("Member", created.AccessId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task ActivateAsync_NonPositiveDuration_Throws_WithoutCallingGraph(int minutes)
    {
        var http = new RecordingPluginHttp(_ => Json(CreatedRequestJson));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.ActivateAsync(
            "prin-1", "group-1", "member", TimeSpan.FromMinutes(minutes), "why", CancellationToken.None));

        Assert.Empty(http.Calls);
    }

    // ---- deactivation -----------------------------------------------------

    [Fact]
    public async Task DeactivateAsync_PostsSelfDeactivate_WithoutAScheduleInfo()
    {
        var http = new RecordingPluginHttp(_ => Json("""
            { "id": "req-2", "status": "Provisioned", "action": "selfDeactivate" }
            """));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.DeactivateAsync("prin-1", "group-1", "Owner", "done", CancellationToken.None);

        var call = Assert.Single(http.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(RequestsUrl, call.Url, StringComparer.Ordinal);

        var body = JsonDocument.Parse(call.Body).RootElement;
        Assert.Equal("selfDeactivate", body.GetProperty("action").GetString());
        Assert.Equal("owner", body.GetProperty("accessId").GetString());
        Assert.Equal("group-1", body.GetProperty("groupId").GetString());
        Assert.False(body.TryGetProperty("scheduleInfo", out _));
    }

    // The undocumented half of the contract: a service that insists on a
    // scheduleInfo gets the degenerate one, once, rather than the user seeing a
    // failed deactivation.
    [Fact]
    public async Task DeactivateAsync_BadRequest_RetriesOnceWithAZeroDurationScheduleInfo()
    {
        var http = new RecordingPluginHttp(call => call.Body.Contains("scheduleInfo", StringComparison.Ordinal)
            ? Json("""{ "id": "req-2", "status": "Provisioned" }""")
            : Status(HttpStatusCode.BadRequest));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var created = await client.DeactivateAsync(
            "prin-1", "group-1", "member", "done", CancellationToken.None);

        Assert.Equal(2, http.Calls.Count);
        Assert.DoesNotContain("scheduleInfo", http.Calls[0].Body, StringComparison.Ordinal);

        var retry = JsonDocument.Parse(http.Calls[1].Body).RootElement;
        Assert.Equal("selfDeactivate", retry.GetProperty("action").GetString());
        var expiration = retry.GetProperty("scheduleInfo").GetProperty("expiration");
        Assert.Equal("afterDuration", expiration.GetProperty("type").GetString());
        Assert.Equal("PT0S", expiration.GetProperty("duration").GetString());
        Assert.DoesNotContain("startDateTime", http.Calls[1].Body, StringComparison.Ordinal);

        Assert.Equal("req-2", created.Id);
    }

    // Only a 400 is the "shape was wrong" signal. A 403 is a permission
    // problem; retrying it would submit the same rejected request twice.
    [Fact]
    public async Task DeactivateAsync_Forbidden_Throws_WithoutRetrying()
    {
        var http = new RecordingPluginHttp(_ => Status(HttpStatusCode.Forbidden));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => client.DeactivateAsync(
            "prin-1", "group-1", "member", "done", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
        Assert.Single(http.Calls);
    }

    // Both attempts rejected: the 400 from the retry is what surfaces, and the
    // caller sees a failure rather than a silently swallowed deactivation.
    [Fact]
    public async Task DeactivateAsync_BothAttemptsRejected_Throws()
    {
        var http = new RecordingPluginHttp(_ => Status(HttpStatusCode.BadRequest));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DeactivateAsync(
            "prin-1", "group-1", "member", "done", CancellationToken.None));

        Assert.Equal(2, http.Calls.Count);
    }

    // A blank reason is omitted rather than sent as an empty string —
    // deactivation is allowed without one, unlike activation.
    [Fact]
    public async Task DeactivateAsync_BlankJustification_IsOmitted()
    {
        var http = new RecordingPluginHttp(_ => Json("""{ "id": "req-2", "status": "Provisioned" }"""));
        var client = new GraphGroupPimClient(NewContext(http), "tenant-1");

        await client.DeactivateAsync("prin-1", "group-1", "member", "   ", CancellationToken.None);

        var body = JsonDocument.Parse(Assert.Single(http.Calls).Body).RootElement;
        Assert.False(body.TryGetProperty("justification", out _));
    }

    // ---- harness ----------------------------------------------------------

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphGroupPimClientActivationTests>.Instance);
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
            """{ "error": { "code": "Request_BadRequest", "message": "invalid" } }""",
            Encoding.UTF8,
            "application/json"),
    };

    private sealed record Call(HttpMethod Method, string Url, string Body);

    // Records the method, URL and serialized body of every call, and replies
    // from a single function of the call just recorded — which is what lets the
    // deactivation retry answer differently to the body it is sent.
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
