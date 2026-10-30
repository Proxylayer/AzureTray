using System;
using System.Collections.Generic;
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

// Search in the Open Request menu, driven end to end: canned Graph/ARM
// responses in, the rows the host would render for a given query out. The menu
// item exposes a SearchProvider instead of Children, so the host draws a search
// box and calls the provider on every keystroke; these tests pin what each
// query leaves visible.
public sealed class PimPluginSearchTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task OpenRequestMenu_IsSearchable_AndSeedsFromTheEmptyQuery()
    {
        var rows = (await QueryAsync(string.Empty))[0];

        Assert.Contains(rows, r => r.Text == "Contoso" && r.Icon == "↻");
        Assert.Contains(rows, r => r.Text == "Entra ID" && !r.IsEnabled);
        Assert.Contains(rows, r => r.Text.StartsWith("Owner (Entra ID directory)", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Text == "Azure RBAC" && !r.IsEnabled);
        Assert.Contains(rows, r => r.Text.StartsWith("Reader (Dev sub)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_ByRoleName_LeavesOnlyMatchingRows_AndDropsEmptySourceGroups()
    {
        var rows = (await QueryAsync("reader"))[0];

        Assert.Contains(rows, r => r.Text.StartsWith("Reader (Dev sub)", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Text.StartsWith("Owner", StringComparison.Ordinal));
        // The Entra ID section emptied out, so its header goes too.
        Assert.DoesNotContain(rows, r => r.Text == "Entra ID");
        Assert.Contains(rows, r => r.Text == "Azure RBAC" && !r.IsEnabled);
        // The tenant refresh header stays while the tenant has matches.
        Assert.Contains(rows, r => r.Text == "Contoso" && r.Icon == "↻");
    }

    // "uat"-style searches work because the scope is matched too, not just the
    // role name — the same role exists at many scopes and the scope is usually
    // what the user is hunting for.
    [Fact]
    public async Task Search_ByScope_FindsTheRole()
    {
        var rows = (await QueryAsync("dev sub"))[0];

        Assert.Contains(rows, r => r.Text.StartsWith("Reader (Dev sub)", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Text.StartsWith("Owner", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Search_WithNoMatchesAnywhere_ShowsASingleDisabledRow()
    {
        var rows = (await QueryAsync("zzz-nothing"))[0];

        var row = Assert.Single(rows);
        Assert.Equal("No matches", row.Text);
        Assert.False(row.IsEnabled);
    }

    // A whitespace-only query is no query: everything stays, including the
    // "No eligible roles" placeholder path exercised elsewhere.
    [Fact]
    public async Task Search_WithWhitespace_ShowsEverything()
    {
        var rows = await QueryAsync(string.Empty, "   ");

        Assert.Equal(
            rows[0].Select(r => r.Text),
            rows[1].Select(r => r.Text));
    }

    // Boots the plugin against one Entra role and one ARM role, forces the
    // eligible-roles poll, and runs every query through the Open Request menu's
    // SearchProvider inside the plugin's lifetime — the provider closes over
    // live watcher state, so results are materialized before shutdown.
    private async Task<IReadOnlyList<PluginMenuItem>[]> QueryAsync(params string[] queries)
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();

        await plugin.InitializeAsync(NewContext(), CancellationToken.None);
        try
        {
            var poll = plugin.Tests.Single(t => t.Name == "Force eligible-roles poll");
            var result = await poll.Run(CancellationToken.None);
            Assert.True(result.Passed, result.Message);

            var openRequest = plugin.GetMenuItems()[1];
            Assert.Null(openRequest.Children);
            Assert.NotNull(openRequest.SearchProvider);
            Assert.Equal("Search roles…", openRequest.SearchPlaceholder);

            return queries
                .Select(q => (IReadOnlyList<PluginMenuItem>)openRequest.SearchProvider!(q).ToArray())
                .ToArray();
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    // ---- canned payloads ----------------------------------------------------

    private const string Empty = """{ "value": [] }""";

    private const string EntraOwnerJson = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "directoryScopeId": "/",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
        } ] }
        """;

    private const string ArmReaderRoleId =
        "/subscriptions/sub-1/providers/Microsoft.Authorization/roleDefinitions/role-reader";

    private const string ArmSubscriptionsJson = """
        { "value": [
            { "id": "/subscriptions/sub-1", "subscriptionId": "sub-1", "displayName": "Dev sub", "state": "Enabled" }
        ] }
        """;

    private static readonly string ArmReaderJson = $$"""
        { "value": [ {
            "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleEligibilitySchedules/elig-arm-1",
            "name": "elig-arm-1",
            "properties": {
              "principalId": "prin-1",
              "roleDefinitionId": "{{ArmReaderRoleId}}",
              "scope": "/subscriptions/sub-1",
              "status": "Active",
              "memberType": "Direct",
              "expandedProperties": {
                "principal": { "id": "prin-1", "displayName": "Alice", "type": "User" },
                "roleDefinition": { "id": "{{ArmReaderRoleId}}", "displayName": "Reader" },
                "scope": { "id": "/subscriptions/sub-1", "displayName": "Dev sub", "type": "subscription" }
              }
            }
        } ] }
        """;

    // ---- harness ------------------------------------------------------------

    private IPluginContext NewContext()
    {
        var tenants = new[] { new PluginTenant("tenant-1", "Contoso") };

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginSearchTests>.Instance);
        ctx.Tenants.Returns(tenants);
        ctx.ReadyTenants.Returns(tenants);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.DataDir.Returns(_dataDir);
        ctx.GetHttpClient(Arg.Any<string>()).Returns(new StubPluginHttp());
        return ctx;
    }

    private sealed class StubPluginHttp : IPluginHttpClient
    {
        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var json = clientName == PluginHttpClientNames.Graph ? Graph(url) : Arm(url);
            return Task.FromResult(new HttpResponseMessage(
                json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json"),
            });
        }

        private static string? Graph(string url)
        {
            if (Has(url, "v1.0/me")) return """{ "id": "prin-1" }""";
            if (Has(url, "roleManagementPolicyAssignments")) return null;
            if (Has(url, "roleEligibilitySchedules")) return EntraOwnerJson;
            if (Has(url, "roleAssignmentScheduleInstances")) return Empty;
            if (Has(url, "roleAssignmentScheduleRequests")) return Empty;
            return null;
        }

        private static string? Arm(string url)
        {
            if (Has(url, "roleManagementPolicyAssignments")) return null;
            if (Has(url, "roleEligibilitySchedules")) return ArmReaderJson;
            if (Has(url, "roleAssignmentScheduleInstances")) return Empty;
            if (Has(url, "roleAssignmentScheduleRequests")) return Empty;
            if (Has(url, "subscriptions?api-version")) return ArmSubscriptionsJson;
            return null;
        }

        private static bool Has(string url, string fragment)
            => url.Contains(fragment, StringComparison.Ordinal);
    }
}
