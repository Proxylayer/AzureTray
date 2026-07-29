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

// The activation-cap suffix on eligible-role rows, driven end to end: canned
// Graph/ARM responses in, exact row text out. The row text is the only slot the
// host gives for right-hand info, so these strings are the contract. The rule
// being pinned is that the marker appears only when a cap was actually read AND
// is tighter than the longest duration otherwise offered — a fallback ceiling
// must never be printed as if it were the role's own policy.
public sealed class PimPluginCapRowTests : IDisposable
{
    private const string EntraRowPrefix = "    Owner  (Entra ID directory)";
    private const string ArmRowPrefix = "    Reader  (Dev sub)";

    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task EligibleRow_WithATightCap_CarriesTheMaxSuffix()
    {
        var row = await RoleRowAsync(
            "Owner",
            new Scenario { GraphEligible = EntraOwnerJson, GraphPolicies = EntraPolicyJson("PT2H") });

        Assert.Equal($"{EntraRowPrefix}  ·  max 2h", row.Text);
    }

    // The cap could not be read (403 / missing rule). Entra activation still
    // clamps to the documented 8h ceiling internally, but the row must NOT say
    // "max 8h" — that would present a service default as this role's policy.
    [Fact]
    public async Task EligibleRow_WithAnUnreadableCap_HasNoMarker()
    {
        var row = await RoleRowAsync(
            "Owner",
            new Scenario { GraphEligible = EntraOwnerJson, GraphPolicies = null });

        Assert.Equal(EntraRowPrefix, row.Text);
        Assert.True(row.IsEnabled);
        Assert.NotNull(row.Invoke);
    }

    // A cap equal to the longest standard step restricts nothing, so the row
    // stays quiet.
    [Fact]
    public async Task EligibleRow_WithACapEqualToTheLongestStandardStep_HasNoMarker()
    {
        var row = await RoleRowAsync(
            "Owner",
            new Scenario { GraphEligible = EntraOwnerJson, GraphPolicies = EntraPolicyJson("PT8H") });

        Assert.Equal(EntraRowPrefix, row.Text);
    }

    [Fact]
    public async Task EligibleRow_WithASubHourCap_RendersTheCompactDuration()
    {
        var row = await RoleRowAsync(
            "Owner",
            new Scenario { GraphEligible = EntraOwnerJson, GraphPolicies = EntraPolicyJson("PT30M") });

        Assert.Equal($"{EntraRowPrefix}  ·  max 30 min", row.Text);
    }

    // The documented example, on the ARM side: caps read from
    // properties.effectiveRules land on the Azure RBAC rows too.
    [Fact]
    public async Task EligibleArmRow_WithATightCap_CarriesTheMaxSuffix()
    {
        var row = await RoleRowAsync(
            "Reader",
            new Scenario
            {
                ArmSubscriptions = ArmSubscriptionsJson,
                ArmEligible = ArmReaderJson,
                ArmPolicies = ArmPolicyJson("PT2H"),
            });

        Assert.Equal($"{ArmRowPrefix}  ·  max 2h", row.Text);
    }

    // Regression pin for the countdown that shipped last session: an active row
    // renders the remaining time and nothing about caps, even when a cap was
    // read for the role. The cap suffix belongs to eligible (inactive) rows only.
    [Fact]
    public async Task ActiveRow_RemainingTimeLabel_IsUnchangedByCapReads()
    {
        var end = DateTimeOffset.UtcNow.AddSeconds((3 * 3600) + (42 * 60) + 5);

        var row = await RoleRowAsync(
            "Owner",
            new Scenario
            {
                GraphEligible = EntraOwnerJson,
                GraphActives = EntraActivesJson(end),
                GraphPolicies = EntraPolicyJson("PT2H"),
            });

        Assert.Equal($"{EntraRowPrefix}  ✓ active · 3h 42m left", row.Text);
        Assert.DoesNotContain("max", row.Text, StringComparison.Ordinal);
        Assert.False(row.IsEnabled);
    }

    [Fact]
    public async Task ActiveRow_WithNoCapRead_StillRendersTheRemainingTime()
    {
        // Seconds of slack on top of the whole minute: FormatRemaining floors,
        // so an end time exactly 47 minutes out renders as "46m left" once the
        // poll and menu build have taken their share of the second.
        var end = DateTimeOffset.UtcNow.AddSeconds((47 * 60) + 5);

        var row = await RoleRowAsync(
            "Owner",
            new Scenario
            {
                GraphEligible = EntraOwnerJson,
                GraphActives = EntraActivesJson(end),
                GraphPolicies = null,
            });

        Assert.Equal($"{EntraRowPrefix}  ✓ active · 47m left", row.Text);
    }

    // The same role reached through a group and directly comes back twice from
    // Graph. The menu must show one row for it — RoleRowAsync's Single() is the
    // assertion — and that row must still carry the cap, since the collapse runs
    // before the policy caps are attached.
    [Fact]
    public async Task EligibleRow_DuplicateGrantPaths_RenderAsOneRowThatKeepsItsCap()
    {
        var row = await RoleRowAsync(
            "Owner",
            new Scenario
            {
                GraphEligible = EntraOwnerTwoGrantPathsJson,
                GraphPolicies = EntraPolicyJson("PT2H"),
            });

        Assert.Equal($"{EntraRowPrefix}  ·  max 2h", row.Text);
    }

    // ---- canned payloads --------------------------------------------------

    private const string Empty = """{ "value": [] }""";

    // One directory-wide eligibility, twice: inherited through a group (no
    // directoryScopeId on the response) and held directly.
    private const string EntraOwnerTwoGrantPathsJson = """
        { "value": [
          {
            "id": "elig-group",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "memberType": "Group",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
          },
          {
            "id": "elig-direct",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "directoryScopeId": "/",
            "memberType": "Direct",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
          }
        ] }
        """;

    private const string EntraOwnerJson = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "directoryScopeId": "/",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
        } ] }
        """;

    private static string EntraActivesJson(DateTimeOffset end) => $$"""
        { "value": [ {
            "id": "inst-1",
            "principalId": "prin-1",
            "roleDefinitionId": "role-owner",
            "directoryScopeId": "/",
            "endDateTime": "{{end:O}}",
            "roleDefinition": { "id": "role-owner", "displayName": "Owner" }
        } ] }
        """;

    private static string EntraPolicyJson(string maximumDuration) => $$"""
        { "value": [ {
            "id": "assign-1",
            "policyId": "pol-1",
            "roleDefinitionId": "role-owner",
            "scopeId": "/",
            "scopeType": "Directory",
            "policy": { "id": "pol-1", "effectiveRules": [
              {
                "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule",
                "id": "Expiration_EndUser_Assignment",
                "maximumDuration": "{{maximumDuration}}",
                "isExpirationRequired": true
              }
            ] }
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

    private static string ArmPolicyJson(string maximumDuration) => $$"""
        { "value": [ {
            "id": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicyAssignments/assign-arm-1",
            "name": "assign-arm-1",
            "properties": {
              "policyId": "/subscriptions/sub-1/providers/Microsoft.Authorization/roleManagementPolicies/pol-arm-1",
              "roleDefinitionId": "{{ArmReaderRoleId}}",
              "effectiveRules": [
                {
                  "id": "Expiration_EndUser_Assignment",
                  "ruleType": "RoleManagementPolicyExpirationRule",
                  "maximumDuration": "{{maximumDuration}}",
                  "isExpirationRequired": true,
                  "target": { "caller": "EndUser", "operations": [ "All" ], "level": "Assignment" }
                }
              ]
            }
        } ] }
        """;

    // ---- harness ----------------------------------------------------------

    // What each canned endpoint returns for one test. A null policy payload
    // means the policy endpoint 404s — the shape a 403 takes from the watcher's
    // point of view: the read throws and the cap stays unknown.
    private sealed class Scenario
    {
        public string GraphEligible { get; init; } = Empty;
        public string GraphActives { get; init; } = Empty;
        public string? GraphPolicies { get; init; }
        public string ArmSubscriptions { get; init; } = Empty;
        public string ArmEligible { get; init; } = Empty;
        public string? ArmPolicies { get; init; }
    }

    // Boots the plugin against the scenario, forces the eligible-roles poll
    // through the plugin's own Test Runner entry, and returns the single role row
    // matching the fragment from the Open Request menu.
    private async Task<PluginMenuItem> RoleRowAsync(string roleNameFragment, Scenario scenario)
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();

        await plugin.InitializeAsync(NewContext(scenario), CancellationToken.None);
        try
        {
            var poll = plugin.Tests.Single(t => t.Name == "Force eligible-roles poll");
            var result = await poll.Run(CancellationToken.None);
            Assert.True(result.Passed, result.Message);

            var openRequest = plugin.GetMenuItems()[1];
            Assert.NotNull(openRequest.Children);
            return openRequest.Children!.Single(
                c => c.Text.Contains(roleNameFragment, StringComparison.Ordinal));
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    private IPluginContext NewContext(Scenario scenario)
    {
        var tenants = new[] { new PluginTenant("tenant-1", "Contoso") };

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginCapRowTests>.Instance);
        ctx.Tenants.Returns(tenants);
        ctx.ReadyTenants.Returns(tenants);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");
        ctx.DataDir.Returns(_dataDir);
        ctx.GetHttpClient(Arg.Any<string>()).Returns(new StubPluginHttp(scenario));
        return ctx;
    }

    // Routes by client name (the production code passes "graph" or "arm") and
    // then by resource segment. An unrouted URL 404s, which each client turns
    // into a thrown HttpRequestException — exactly how a real permission denial
    // reaches the watcher.
    private sealed class StubPluginHttp : IPluginHttpClient
    {
        private readonly Scenario _scenario;

        public StubPluginHttp(Scenario scenario) { _scenario = scenario; }

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Both PIM clients build relative URIs, so ToString() is the only
            // safe accessor (AbsoluteUri throws on a relative Uri).
            var url = request.RequestUri?.ToString() ?? string.Empty;
            var json = clientName == PluginHttpClientNames.Graph ? Graph(url) : Arm(url);
            return Task.FromResult(new HttpResponseMessage(
                json is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            {
                Content = new StringContent(json ?? "{}", Encoding.UTF8, "application/json"),
            });
        }

        private string? Graph(string url)
        {
            if (Has(url, "v1.0/me")) return """{ "id": "prin-1" }""";
            if (Has(url, "roleManagementPolicyAssignments")) return _scenario.GraphPolicies;
            if (Has(url, "roleEligibilitySchedules")) return _scenario.GraphEligible;
            if (Has(url, "roleAssignmentScheduleInstances")) return _scenario.GraphActives;
            if (Has(url, "roleAssignmentScheduleRequests")) return Empty;
            return null;
        }

        private string? Arm(string url)
        {
            if (Has(url, "roleManagementPolicyAssignments")) return _scenario.ArmPolicies;
            if (Has(url, "roleEligibilitySchedules")) return _scenario.ArmEligible;
            if (Has(url, "roleAssignmentScheduleInstances")) return Empty;
            if (Has(url, "roleAssignmentScheduleRequests")) return Empty;
            if (Has(url, "subscriptions?api-version")) return _scenario.ArmSubscriptions;
            return null;
        }

        private static bool Has(string url, string fragment)
            => url.Contains(fragment, StringComparison.Ordinal);
    }
}
