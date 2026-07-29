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
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// What actually goes on the wire for a self-activation / self-deactivation, with
// directoryScopeId as the subject. Sending "/" for an administrative-unit-scoped
// eligibility asks for a grant the user does not hold, so the role's own scope
// has to reach the body — while the directory-wide body, which is what every
// activation sent before, must be byte-for-byte what it always was.
public sealed class GraphPimClientActivationScopeTests
{
    private const string RequestsUrl = "v1.0/roleManagement/directory/roleAssignmentScheduleRequests";
    private const string AuScope = "/administrativeUnits/au-1";

    // ---- activation -------------------------------------------------------

    // The regression pin: the common path did not shift. Every field of the
    // directory-wide activation body, asserted whole.
    [Fact]
    public async Task ActivateRoleAsync_DirectoryWideRole_SendsTheSameBodyAsBefore()
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ActivateRoleAsync(
            "prin-1", "role-owner", "/", TimeSpan.FromHours(4), "incident #42", CancellationToken.None);

        Assert.Equal(RequestsUrl, Assert.Single(http.Urls));
        var body = Body(http);
        Assert.Equal("selfActivate", body.GetProperty("action").GetString());
        Assert.Equal("prin-1", body.GetProperty("principalId").GetString());
        Assert.Equal("role-owner", body.GetProperty("roleDefinitionId").GetString());
        Assert.Equal("/", body.GetProperty("directoryScopeId").GetString());
        Assert.Equal("incident #42", body.GetProperty("justification").GetString());

        var schedule = body.GetProperty("scheduleInfo");
        // Omitted on purpose: a startDateTime of "now" is already in the past by
        // the time Graph reads it, and Graph 400s on that.
        Assert.False(schedule.TryGetProperty("startDateTime", out _));
        Assert.Equal("afterDuration", schedule.GetProperty("expiration").GetProperty("type").GetString());
        Assert.Equal("PT4H", schedule.GetProperty("expiration").GetProperty("duration").GetString());
    }

    [Fact]
    public async Task ActivateRoleAsync_AdministrativeUnitScopedRole_SendsThatScope()
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ActivateRoleAsync(
            "prin-1", "role-groups-admin", AuScope, TimeSpan.FromHours(1), "audit", CancellationToken.None);

        Assert.Equal(AuScope, Body(http).GetProperty("directoryScopeId").GetString());
    }

    // The wire-level defence: a cache row written before the scope was persisted,
    // or a response that omitted it, still has to produce a valid request.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ActivateRoleAsync_AbsentScope_FallsBackToTheDirectoryScope(string? scope)
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ActivateRoleAsync(
            "prin-1", "role-owner", scope, TimeSpan.FromHours(1), "audit", CancellationToken.None);

        Assert.Equal("/", Body(http).GetProperty("directoryScopeId").GetString());
    }

    [Fact]
    public async Task ActivateRoleAsync_TrimsTheScope()
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.ActivateRoleAsync(
            "prin-1", "role-groups-admin", $"  {AuScope}  ", TimeSpan.FromHours(1), "audit", CancellationToken.None);

        Assert.Equal(AuScope, Body(http).GetProperty("directoryScopeId").GetString());
    }

    // ---- deactivation -----------------------------------------------------

    [Fact]
    public async Task DeactivateRoleAsync_DirectoryWideRole_SendsTheSameBodyAsBefore()
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.DeactivateRoleAsync(
            "prin-1", "role-owner", "/", "Deactivated from AzureTray.", CancellationToken.None);

        Assert.Equal(RequestsUrl, Assert.Single(http.Urls));
        var body = Body(http);
        Assert.Equal("selfDeactivate", body.GetProperty("action").GetString());
        Assert.Equal("prin-1", body.GetProperty("principalId").GetString());
        Assert.Equal("role-owner", body.GetProperty("roleDefinitionId").GetString());
        Assert.Equal("/", body.GetProperty("directoryScopeId").GetString());
        Assert.Equal("Deactivated from AzureTray.", body.GetProperty("justification").GetString());
        // selfDeactivate is immediate — no schedule.
        Assert.False(body.TryGetProperty("scheduleInfo", out _));
    }

    [Fact]
    public async Task DeactivateRoleAsync_AdministrativeUnitScopedRole_SendsThatScope()
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.DeactivateRoleAsync(
            "prin-1", "role-groups-admin", AuScope, "done", CancellationToken.None);

        Assert.Equal(AuScope, Body(http).GetProperty("directoryScopeId").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DeactivateRoleAsync_AbsentScope_FallsBackToTheDirectoryScope(string? scope)
    {
        var http = NewHttp();
        var client = new GraphPimClient(NewContext(http), "tenant-1");

        await client.DeactivateRoleAsync(
            "prin-1", "role-owner", scope, "done", CancellationToken.None);

        Assert.Equal("/", Body(http).GetProperty("directoryScopeId").GetString());
    }

    // ---- harness ----------------------------------------------------------

    private static JsonElement Body(RecordingPluginHttp http)
        => JsonDocument.Parse(Assert.Single(http.Bodies)).RootElement;

    private static RecordingPluginHttp NewHttp()
        => new("""{ "id": "req-1", "status": "Granted" }""");

    private static IPluginContext NewContext(IPluginHttpClient http)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);
        ctx.Logger.Returns(NullLogger<GraphPimClientActivationScopeTests>.Instance);
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.Tenants.Returns(new List<PluginTenant>());
        return ctx;
    }

    // Records the URL and the serialized request body of every call, and answers
    // each with the same canned response.
    private sealed class RecordingPluginHttp : IPluginHttpClient
    {
        private readonly string _responseJson;

        public RecordingPluginHttp(string responseJson) { _responseJson = responseJson; }

        public List<string> Urls { get; } = new();

        public List<string> Bodies { get; } = new();

        public async Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // The PIM clients build relative URIs, so ToString() is the only safe
            // accessor (AbsoluteUri throws on a relative Uri).
            Urls.Add(request.RequestUri?.ToString() ?? string.Empty);
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
