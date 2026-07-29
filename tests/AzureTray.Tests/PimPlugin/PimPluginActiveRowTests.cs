using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Rendering of the eligible-role rows in the Open Request menu. The row text is
// the only place the host has for right-hand info, so the active marker and its
// countdown are baked into it — these tests pin the exact strings.
public sealed class PimPluginActiveRowTests : IDisposable
{
    private const string RowPrefix = "    Owner  (Entra ID directory)";

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task ActiveRow_CarriesTheRemainingTimeSuffix()
    {
        var end = DateTimeOffset.UtcNow.AddSeconds((3 * 3600) + (42 * 60) + 5);

        var row = await RoleRowAsync(ActivesJson(end));

        Assert.Equal($"{RowPrefix}  ✓ active · 3h 42m left", row.Text);
    }

    // Unchanged from before the countdown existed: an inactive row is clickable
    // and carries no marker at all.
    [Fact]
    public async Task InactiveRow_IsUnchanged()
    {
        var row = await RoleRowAsync(NoActivesJson);

        Assert.Equal(RowPrefix, row.Text);
        Assert.True(row.IsEnabled);
        Assert.NotNull(row.Invoke);
        Assert.NotNull(row.ContextItems);
        Assert.Single(row.ContextItems!);
        Assert.Equal("Copy role name", row.ContextItems![0].Text);
    }

    // A permanent (never-expiring) assignment has no end time to count down.
    [Fact]
    public async Task ActiveRow_WithNoEndTime_DegradesToThePlainMarker()
    {
        var row = await RoleRowAsync(ActivesJson(end: null));

        Assert.Equal($"{RowPrefix}  ✓ active", row.Text);
    }

    // An end time already in the past must never render as a negative duration.
    [Fact]
    public async Task ActiveRow_WithExpiredEndTime_DegradesToThePlainMarker()
    {
        var row = await RoleRowAsync(ActivesJson(DateTimeOffset.UtcNow.AddMinutes(-5)));

        Assert.Equal($"{RowPrefix}  ✓ active", row.Text);
    }

    [Fact]
    public async Task ActiveRow_IsNotClickable_ButOffersDeactivate()
    {
        var row = await RoleRowAsync(ActivesJson(DateTimeOffset.UtcNow.AddHours(1)));

        Assert.False(row.IsEnabled);
        Assert.Null(row.Invoke);
        Assert.NotNull(row.ContextItems);
        Assert.Contains(row.ContextItems!, c => c.Text == "Copy role name");
        Assert.Contains(row.ContextItems!, c => c.Text == "Deactivate");
    }

    [Fact]
    public async Task ActiveRow_SubHourRemaining_RendersMinutesOnly()
    {
        var end = DateTimeOffset.UtcNow.AddSeconds((47 * 60) + 5);

        var row = await RoleRowAsync(ActivesJson(end));

        Assert.Equal($"{RowPrefix}  ✓ active · 47m left", row.Text);
    }

    // ---- harness ----------------------------------------------------------

    private const string NoActivesJson = """{ "value": [] }""";

    private const string EligibleJson = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "directoryScopeId": "/",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
        } ] }
        """;

    private static string ActivesJson(DateTimeOffset? end)
    {
        var endLiteral = end is { } e ? $"\"{e:O}\"" : "null";
        return $$"""
            { "value": [ {
                "id": "inst-1",
                "principalId": "prin-1",
                "roleDefinitionId": "role-owner",
                "directoryScopeId": "/",
                "endDateTime": {{endLiteral}},
                "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
            } ] }
            """;
    }

    // Boots the plugin against canned Graph/ARM responses, forces the
    // eligible-roles poll through the plugin's own Test Runner entry, and
    // returns the single Entra role row from the Open Request menu.
    private async Task<PluginMenuItem> RoleRowAsync(string activesJson)
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        var context = NewContext(activesJson);

        await plugin.InitializeAsync(context, CancellationToken.None);
        try
        {
            var poll = plugin.Tests.Single(t => t.Name == "Force eligible-roles poll");
            var result = await poll.Run(CancellationToken.None);
            Assert.True(result.Passed, result.Message);

            var openRequest = plugin.GetMenuItems()[1];
            Assert.NotNull(openRequest.Children);
            return openRequest.Children!.Single(
                c => c.Text.Contains("Owner", StringComparison.Ordinal));
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    private IPluginContext NewContext(string activesJson)
    {
        var tenants = new[] { new PluginTenant("tenant-1", "Contoso") };

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginActiveRowTests>.Instance);
        ctx.Tenants.Returns(tenants);
        ctx.ReadyTenants.Returns(tenants);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.DataDir.Returns(_dataDir);
        ctx.GetHttpClient(Arg.Any<string>()).Returns(new StubPluginHttp(activesJson));
        return ctx;
    }

    // Canned Graph/ARM responses: one Entra eligible role, the actives feed the
    // test supplies, and empty everything else.
    private sealed class StubPluginHttp : IPluginHttpClient
    {
        private readonly string _activesJson;

        public StubPluginHttp(string activesJson) { _activesJson = activesJson; }

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Both PIM clients build relative URIs, so ToString() is the only
            // safe accessor here (AbsoluteUri throws on a relative Uri).
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var json = Reply(url);
            return Task.FromResult(new HttpResponseMessage(
                json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json"),
            });
        }

        private string? Reply(string url)
        {
            if (url.Contains("v1.0/me", StringComparison.Ordinal)) return """{ "id": "prin-1" }""";
            if (url.Contains("roleEligibilitySchedules", StringComparison.Ordinal)) return EligibleJson;
            if (url.Contains("roleAssignmentScheduleInstances", StringComparison.Ordinal)) return _activesJson;
            if (url.Contains("roleAssignmentScheduleRequests", StringComparison.Ordinal)) return NoActivesJson;
            if (url.Contains("subscriptions?api-version", StringComparison.Ordinal)) return NoActivesJson;
            return null;
        }
    }
}
