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

// PIM for Groups rows in the Open Request menu, driven end to end: canned Graph
// responses in, menu items out.
//
// Two things are pinned. The rows sit under their own "Entra Groups" header,
// after the Entra ID and Azure RBAC groups, so a user can tell a group
// membership from a directory role at a glance — every group row otherwise
// reads only "Member" or "Owner".
//
// And the row Key must be distinct per row. Key is what the host re-anchors the
// menu on between rebuilds; for the other two sources the role definition id
// carries that identity, but a group row's is only ever "member" or "owner", so
// the group id has to be part of it. Colliding keys would make two different
// groups' rows the same row to the host.
public sealed class PimPluginGroupRowTests : IDisposable
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
    public async Task GroupRow_RendersTheAccessAndTheGroupName()
    {
        var children = await OpenRequestChildrenAsync(new Scenario { GroupEligible = OneGroupJson });

        var row = Assert.Single(children, c => c.Text.StartsWith("Member (", StringComparison.Ordinal));
        Assert.Equal("Member (Contoso SQL Admins)", row.Text);
        Assert.True(row.IsEnabled);
        Assert.NotNull(row.Invoke);
    }

    [Fact]
    public async Task GroupRows_AppearUnderTheEntraGroupsHeader_AfterTheOtherSources()
    {
        var children = await OpenRequestChildrenAsync(new Scenario
        {
            GraphEligible = EntraOwnerJson,
            GroupEligible = OneGroupJson,
        });

        var texts = children.Select(c => c.Text).ToList();
        var entraHeader = texts.IndexOf("Entra ID");
        var groupHeader = texts.IndexOf("Entra Groups");
        var groupRow = texts.IndexOf("Member (Contoso SQL Admins)");

        Assert.True(entraHeader >= 0, "the Entra ID header is missing");
        Assert.True(groupHeader > entraHeader, "the Entra Groups header must follow the Entra ID group");
        Assert.Equal(groupHeader + 1, groupRow);

        // Headers are labels, not actions.
        Assert.False(children[groupHeader].IsEnabled);
    }

    // No group access, no header: an empty section would be noise in a menu
    // that most users open to click one row.
    [Fact]
    public async Task NoGroupAccess_OmitsTheEntraGroupsHeader()
    {
        var children = await OpenRequestChildrenAsync(new Scenario { GraphEligible = EntraOwnerJson });

        Assert.DoesNotContain(children, c => c.Text == "Entra Groups");
    }

    // The collision that the group id in the key prevents: two groups, same
    // access, identical role definition id.
    [Fact]
    public async Task TwoGroups_SameAccess_GetDistinctRowKeys()
    {
        var children = await OpenRequestChildrenAsync(new Scenario { GroupEligible = TwoGroupsJson });

        var keys = GroupRowKeys(children);

        Assert.Equal(2, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("pim.role.tenant-1.EntraGroup.member.group-1", keys, StringComparer.Ordinal);
        Assert.Contains("pim.role.tenant-1.EntraGroup.member.group-2", keys, StringComparer.Ordinal);
    }

    // The other half: one group, both accesses. The access id is what separates
    // these, and it must not be dropped from the key either.
    [Fact]
    public async Task MemberAndOwnerOnOneGroup_GetDistinctRowKeys()
    {
        var children = await OpenRequestChildrenAsync(new Scenario { GroupEligible = MemberAndOwnerJson });

        var keys = GroupRowKeys(children);

        Assert.Equal(2, keys.Count);
        Assert.Contains("pim.role.tenant-1.EntraGroup.member.group-1", keys, StringComparer.Ordinal);
        Assert.Contains("pim.role.tenant-1.EntraGroup.owner.group-1", keys, StringComparer.Ordinal);
    }

    // A group row's key must not collide with a directory-role row that happens
    // to share the role-definition text.
    [Fact]
    public async Task GroupRowKey_DoesNotCollideWithADirectoryRoleRow()
    {
        var children = await OpenRequestChildrenAsync(new Scenario
        {
            GraphEligible = EntraMemberRoleJson,
            GroupEligible = OneGroupJson,
        });

        var keys = children
            .Where(c => c.Key is not null && c.Key.StartsWith("pim.role.", StringComparison.Ordinal))
            .Select(c => c.Key!)
            .ToList();

        Assert.Equal(2, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    // The host re-anchors on Key between rebuilds, so it must not drift when
    // nothing about the row changed.
    [Fact]
    public async Task GroupRowKeys_AreStableAcrossMenuRebuilds()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        await plugin.InitializeAsync(
            NewContext(new Scenario { GroupEligible = MemberAndOwnerJson }), CancellationToken.None);
        try
        {
            await ForcePollAsync(plugin);

            var first = GroupRowKeys(OpenRequestChildren(plugin));
            var second = GroupRowKeys(OpenRequestChildren(plugin));

            Assert.Equal(first, second);
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    // Group rows all read "Member" or "Owner", so ordering by role name alone
    // would interleave two groups' rows; the group name sorts first.
    [Fact]
    public async Task GroupRows_SortByGroupThenAccess()
    {
        var children = await OpenRequestChildrenAsync(new Scenario { GroupEligible = TwoGroupsBothAccessesJson });

        var groupRows = children
            .Where(c => c.Key is not null
                && c.Key.StartsWith("pim.role.tenant-1.EntraGroup.", StringComparison.Ordinal))
            .Select(c => c.Text)
            .ToList();

        Assert.Equal(
            new[]
            {
                "Member (Alpha Admins)",
                "Owner (Alpha Admins)",
                "Member (Beta Admins)",
            },
            groupRows);
    }

    // An active group membership grays its row out and offers Deactivate,
    // exactly as the other two sources do.
    [Fact]
    public async Task ActiveGroupRow_IsGrayedOut_AndOffersDeactivate()
    {
        var end = DateTimeOffset.UtcNow.AddSeconds((2 * 3600) + (30 * 60) + 5);

        var children = await OpenRequestChildrenAsync(new Scenario
        {
            GroupEligible = OneGroupJson,
            GroupActives = GroupActivesJson(end),
        });

        var row = Assert.Single(children, c => c.Text.StartsWith("Member (", StringComparison.Ordinal));
        Assert.Equal("Member (Contoso SQL Admins) — active, 2h 30m left", row.Text);
        Assert.False(row.IsEnabled);
        Assert.NotNull(row.ContextItems);
        Assert.Contains(row.ContextItems!, c => c.Text == "Deactivate");
    }

    // The cap suffix works the same way on a group row: printed only when a
    // policy was actually read and is tighter than the longest standard step.
    [Fact]
    public async Task GroupRow_WithATightPolicyCap_CarriesTheMaxSuffix()
    {
        var children = await OpenRequestChildrenAsync(new Scenario
        {
            GroupEligible = OneGroupJson,
            GroupPolicies = GroupPolicyJson("PT2H"),
        });

        var row = Assert.Single(children, c => c.Text.StartsWith("Member (", StringComparison.Ordinal));
        Assert.Equal("Member (Contoso SQL Admins) (max 2h)", row.Text);
    }

    // ---- canned payloads --------------------------------------------------

    private const string Empty = """{ "value": [] }""";

    private const string OneGroupJson = """
        { "value": [ {
            "id": "elig-g1",
            "principalId": "prin-1",
            "accessId": "member",
            "groupId": "group-1",
            "memberType": "Direct",
            "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
        } ] }
        """;

    private const string TwoGroupsJson = """
        { "value": [
          {
            "id": "elig-g1", "principalId": "prin-1", "accessId": "member", "groupId": "group-1",
            "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
          },
          {
            "id": "elig-g2", "principalId": "prin-1", "accessId": "member", "groupId": "group-2",
            "group": { "id": "group-2", "displayName": "Contoso Net Admins" }
          }
        ] }
        """;

    private const string MemberAndOwnerJson = """
        { "value": [
          {
            "id": "elig-g1", "principalId": "prin-1", "accessId": "member", "groupId": "group-1",
            "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
          },
          {
            "id": "elig-g2", "principalId": "prin-1", "accessId": "owner", "groupId": "group-1",
            "group": { "id": "group-1", "displayName": "Contoso SQL Admins" }
          }
        ] }
        """;

    private const string TwoGroupsBothAccessesJson = """
        { "value": [
          {
            "id": "elig-g1", "principalId": "prin-1", "accessId": "member", "groupId": "group-2",
            "group": { "id": "group-2", "displayName": "Beta Admins" }
          },
          {
            "id": "elig-g2", "principalId": "prin-1", "accessId": "owner", "groupId": "group-1",
            "group": { "id": "group-1", "displayName": "Alpha Admins" }
          },
          {
            "id": "elig-g3", "principalId": "prin-1", "accessId": "member", "groupId": "group-1",
            "group": { "id": "group-1", "displayName": "Alpha Admins" }
          }
        ] }
        """;

    private static string GroupActivesJson(DateTimeOffset end) => $$"""
        { "value": [ {
            "id": "inst-g1",
            "principalId": "prin-1",
            "accessId": "member",
            "groupId": "group-1",
            "assignmentType": "Activated",
            "startDateTime": "2026-01-01T00:00:00Z",
            "endDateTime": "{{end:O}}"
        } ] }
        """;

    private static string GroupPolicyJson(string maximumDuration) => $$"""
        { "value": [ {
            "id": "assign-g1",
            "policyId": "pol-g1",
            "roleDefinitionId": "member",
            "scopeId": "group-1",
            "scopeType": "Group",
            "policy": { "id": "pol-g1", "rules": [
              {
                "@odata.type": "#microsoft.graph.unifiedRoleManagementPolicyExpirationRule",
                "id": "Expiration_EndUser_Assignment",
                "maximumDuration": "{{maximumDuration}}"
              }
            ] }
        } ] }
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

    // A directory role whose role definition id is the literal "member" — the
    // shape that would collide with a group row if Source were not in the key.
    private const string EntraMemberRoleJson = """
        { "value": [ {
            "id": "elig-1",
            "principalId": "prin-1",
            "roleDefinitionId": "member",
            "directoryScopeId": "/",
            "roleDefinition": { "id": "member", "displayName": "Member" }
        } ] }
        """;

    // ---- harness ----------------------------------------------------------

    private static List<string> GroupRowKeys(IReadOnlyList<PluginMenuItem> children)
        => children
            .Where(c => c.Key is not null
                && c.Key.StartsWith("pim.role.tenant-1.EntraGroup.", StringComparison.Ordinal))
            .Select(c => c.Key!)
            .ToList();

    // What each canned endpoint returns for one test. A null policy payload
    // means the policy endpoint 404s — the shape a 403 takes from the watcher's
    // point of view: the read throws and the cap stays unknown.
    private sealed class Scenario
    {
        public string GraphEligible { get; init; } = Empty;
        public string GraphActives { get; init; } = Empty;
        public string? GraphPolicies { get; init; }
        public string GroupEligible { get; init; } = Empty;
        public string GroupActives { get; init; } = Empty;
        public string? GroupPolicies { get; init; }
    }

    private async Task<IReadOnlyList<PluginMenuItem>> OpenRequestChildrenAsync(Scenario scenario)
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();

        await plugin.InitializeAsync(NewContext(scenario), CancellationToken.None);
        try
        {
            await ForcePollAsync(plugin);
            return OpenRequestChildren(plugin);
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    private static async Task ForcePollAsync(AzureTray.Plugin.PIM.PimPlugin plugin)
    {
        var poll = plugin.Tests.Single(t => t.Name == "Force eligible-roles poll");
        var result = await poll.Run(CancellationToken.None);
        Assert.True(result.Passed, result.Message);
    }

    private static IReadOnlyList<PluginMenuItem> OpenRequestChildren(AzureTray.Plugin.PIM.PimPlugin plugin)
    {
        var openRequest = plugin.GetMenuItems()[1];
        Assert.NotNull(openRequest.Children);
        return openRequest.Children!;
    }

    private IPluginContext NewContext(Scenario scenario)
    {
        var tenants = new[] { new PluginTenant("tenant-1", "Contoso") };

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginGroupRowTests>.Instance);
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
    // then by resource segment. The two policy reads share one collection and
    // are told apart by the scopeType in the filter. An unrouted URL 404s, which
    // each client turns into a thrown HttpRequestException — exactly how a real
    // permission denial reaches the watcher.
    private sealed class StubPluginHttp : IPluginHttpClient
    {
        private readonly Scenario _scenario;

        public StubPluginHttp(Scenario scenario) { _scenario = scenario; }

        public Task<HttpResponseMessage> SendAsync(
            string clientName, string scope, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Both PIM clients build relative URIs, so ToString() is the only
            // safe accessor (AbsoluteUri throws on a relative Uri).
            var url = Uri.UnescapeDataString(request.RequestUri?.ToString() ?? string.Empty);
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
            if (Has(url, "privilegedAccess/group/eligibilityScheduleInstances")) return _scenario.GroupEligible;
            if (Has(url, "privilegedAccess/group/assignmentScheduleInstances")) return _scenario.GroupActives;
            if (Has(url, "privilegedAccess/group/assignmentApprovals")) return Empty;
            if (Has(url, "privilegedAccess/group/assignmentScheduleRequests")) return Empty;
            if (Has(url, "roleManagementPolicyAssignments"))
            {
                return Has(url, "scopeType eq 'Group'") ? _scenario.GroupPolicies : _scenario.GraphPolicies;
            }
            if (Has(url, "roleEligibilitySchedules")) return _scenario.GraphEligible;
            if (Has(url, "roleAssignmentScheduleInstances")) return _scenario.GraphActives;
            if (Has(url, "roleAssignmentScheduleRequests")) return Empty;
            return null;
        }

        private static string? Arm(string url)
        {
            if (Has(url, "subscriptions?api-version")) return Empty;
            if (Has(url, "roleEligibilitySchedules")) return Empty;
            if (Has(url, "roleAssignmentScheduleInstances")) return Empty;
            if (Has(url, "roleAssignmentScheduleRequests")) return Empty;
            return null;
        }

        private static bool Has(string url, string fragment)
            => url.Contains(fragment, StringComparison.Ordinal);
    }
}
