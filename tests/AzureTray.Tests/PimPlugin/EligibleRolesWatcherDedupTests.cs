using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Policies;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The collapse as the menu sees it: duplicates must be gone from
// CurrentEligibleRoles whether they came from the Graph fetch, the ARM fan-out,
// or a cache file written before the collapse existed — and the policy caps must
// still land on the surviving row, which only holds because the dedup key is the
// same key the policy lookup uses.
public sealed class EligibleRolesWatcherDedupTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    private static readonly PluginTenant Tenant = new("tenant-1", "Contoso");

    private const string MgScope = "/providers/Microsoft.Management/managementGroups/mg-1";
    private const string SubScope = "/subscriptions/sub-1";
    private const string OtherSubScope = "/subscriptions/sub-2";
    private const string AuScope = "/administrativeUnits/au-1";

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- Graph projection -------------------------------------------------

    // One role reachable through a group and directly: Graph's principalId filter
    // includes inherited eligibilities, so it comes back twice.
    [Fact]
    public async Task PollAsync_GraphDuplicatesFromTwoGrantPaths_CollapseToOneRow()
    {
        var graph = NewGraph(eligible: new[]
        {
            GraphEligible("Global Reader", "role-global-reader", memberType: "Group", eligibilityId: "elig-group"),
            GraphEligible("Global Reader", "role-global-reader", memberType: "Direct", eligibilityId: "elig-direct"),
        });

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Global Reader", row.RoleName);
        // The Direct row is the one whose eligibility id names the user.
        Assert.Equal("elig-direct", row.EligibilityId);
    }

    // The scope is part of the Entra key, so an administrative-unit-scoped
    // eligibility survives alongside the directory-wide one — and labels itself.
    [Fact]
    public async Task PollAsync_EntraRowsAtDifferentDirectoryScopes_BothSurvive()
    {
        var graph = NewGraph(eligible: new[]
        {
            GraphEligible("Groups Administrator", "role-groups-admin", directoryScopeId: "/"),
            GraphEligible("Groups Administrator", "role-groups-admin", directoryScopeId: AuScope),
        });

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
        Assert.Equal(
            "Entra ID directory",
            Assert.Single(watcher.CurrentEligibleRoles, r => r.DirectoryScopeId == "/").ScopeDisplay);
        Assert.Equal(
            "Administrative unit au-1",
            Assert.Single(watcher.CurrentEligibleRoles, r => r.DirectoryScopeId == AuScope).ScopeDisplay);
    }

    // ---- ARM projection ---------------------------------------------------

    // The fan-out: four subscriptions queried, one management-group-scoped
    // eligibility returned under each of them.
    [Fact]
    public async Task PollAsync_ArmFanOutDuplicates_CollapseToOneRow()
    {
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2"), ArmSub("sub-3"), ArmSub("sub-4") },
            eligible: Enumerable.Range(0, 4)
                .Select(_ => ArmEligible("Reader", "role-reader", MgScope))
                .ToList());

        var watcher = NewWatcher(NewGraph(), arm);
        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal(MgScope, row.ArmScope);
    }

    [Fact]
    public async Task PollAsync_ArmRowsAtDifferentScopes_BothSurvive()
    {
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2") },
            eligible: new[]
            {
                ArmEligible("Reader", "role-reader", SubScope),
                ArmEligible("Reader", "role-reader", OtherSubScope),
            });

        var watcher = NewWatcher(NewGraph(), arm);
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.ArmScope == SubScope);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.ArmScope == OtherSubScope);
    }

    // Both providers duplicating at once: the eligible count is what the menu
    // header shows, so it must reflect the collapsed list.
    [Fact]
    public async Task PollAsync_DuplicatesFromBothProviders_DropTheEligibleCount()
    {
        var graph = NewGraph(eligible: new[]
        {
            GraphEligible("Global Reader", "role-global-reader", eligibilityId: "elig-a"),
            GraphEligible("Global Reader", "role-global-reader", eligibilityId: "elig-b"),
        });
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2"), ArmSub("sub-3") },
            eligible: Enumerable.Range(0, 3)
                .Select(_ => ArmEligible("Reader", "role-reader", MgScope))
                .ToList());

        var watcher = NewWatcher(graph, arm);
        await watcher.PollAsync(CancellationToken.None);

        // Five rows in, two distinct roles out.
        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
    }

    // ---- caps after the collapse ------------------------------------------

    // Dedup runs before AttachEntraCapsAsync, so the surviving row must still be
    // capped — the policy join keys on role definition id, which the collapse
    // preserves.
    [Fact]
    public async Task PollAsync_CollapsedEntraRow_StillCarriesItsPolicyCap()
    {
        var graph = NewGraph(eligible: new[]
        {
            GraphEligible("Global Reader", "role-global-reader", eligibilityId: "elig-a"),
            GraphEligible("Global Reader", "role-global-reader", eligibilityId: "elig-b"),
        });
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies(("role-global-reader", TimeSpan.FromHours(2))));

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(
            TimeSpan.FromHours(2),
            Assert.Single(watcher.CurrentEligibleRoles).MaxActivationDuration);
    }

    // The ARM half of the same guarantee, and the reason the ARM key IS the
    // policy key: the collapsed row must still hit the policy entry for its scope
    // and role definition.
    [Fact]
    public async Task PollAsync_CollapsedArmRow_StillCarriesItsPolicyCap()
    {
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2") },
            eligible: new[]
            {
                ArmEligible("Reader", "role-reader", MgScope),
                ArmEligible("Reader", "role-reader", MgScope),
            });
        arm.GetRolePoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ArmPolicies((MgScope, "role-reader", TimeSpan.FromHours(4))));

        var watcher = NewWatcher(NewGraph(), arm);
        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(
            TimeSpan.FromHours(4),
            Assert.Single(watcher.CurrentEligibleRoles).MaxActivationDuration);
    }

    // ---- the collapsed row still grays out --------------------------------

    // Regression pin: the surviving ARM row keeps the scope its assignment is
    // matched on, so an assignment at (or above) that scope still gray it out.
    [Fact]
    public async Task PollAsync_CollapsedArmRow_IsStillMatchedByItsActiveAssignment()
    {
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1"), ArmSub("sub-2") },
            eligible: new[]
            {
                ArmEligible("Reader", "role-reader", MgScope),
                ArmEligible("Reader", "role-reader", MgScope),
            },
            actives: new[] { ArmActive("Reader", "role-reader", MgScope, DateTimeOffset.UtcNow.AddHours(1)) });

        var watcher = NewWatcher(NewGraph(), arm);
        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.NotNull(watcher.FindActiveFor(row));
    }

    [Fact]
    public async Task PollAsync_CollapsedEntraRow_IsStillMatchedByItsActiveAssignment()
    {
        var graph = NewGraph(eligible: new[]
        {
            GraphEligible("Global Reader", "role-global-reader", memberType: "Group", eligibilityId: "elig-a"),
            GraphEligible("Global Reader", "role-global-reader", memberType: "Direct", eligibilityId: "elig-b"),
        });
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { GraphEligible("Global Reader", "role-global-reader", eligibilityId: "inst-1") });

        var watcher = NewWatcher(graph, NewArm());
        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.NotNull(watcher.FindActiveFor(row));
    }

    // ---- the cache --------------------------------------------------------

    // A cache file written before the collapse existed carries the duplicates.
    // Hydration dedupes on the way in too, or the menu would show them for the
    // half hour until the first poll lands.
    [Fact]
    public async Task Start_CacheContainingDuplicates_HydratesCollapsed()
    {
        WriteCache("""
            {
              "Roles": [
                {
                  "Source": 0,
                  "RoleName": "Global Reader",
                  "RoleDefinitionId": "role-global-reader",
                  "ScopeDisplay": "Entra ID directory",
                  "ArmScope": null,
                  "EligibilityId": "elig-group",
                  "MaxActivationDuration": null,
                  "MemberType": "Group",
                  "DirectoryScopeId": null
                },
                {
                  "Source": 0,
                  "RoleName": "Global Reader",
                  "RoleDefinitionId": "role-global-reader",
                  "ScopeDisplay": "Entra ID directory",
                  "ArmScope": null,
                  "EligibilityId": "elig-direct",
                  "MaxActivationDuration": "02:00:00",
                  "MemberType": "Direct",
                  "DirectoryScopeId": "/"
                },
                {
                  "Source": 1,
                  "RoleName": "Reader",
                  "RoleDefinitionId": "role-reader",
                  "ScopeDisplay": "Prod MG",
                  "ArmScope": "/providers/Microsoft.Management/managementGroups/mg-1",
                  "EligibilityId": "elig-mg",
                  "MaxActivationDuration": "08:00:00",
                  "MemberType": "Direct",
                  "DirectoryScopeId": null
                },
                {
                  "Source": 1,
                  "RoleName": "Reader",
                  "RoleDefinitionId": "role-reader",
                  "ScopeDisplay": "Prod MG",
                  "ArmScope": "/providers/Microsoft.Management/managementGroups/mg-1",
                  "EligibilityId": "elig-mg",
                  "MaxActivationDuration": "08:00:00",
                  "MemberType": "Direct",
                  "DirectoryScopeId": null
                }
              ],
              "RelevantSubscriptionIds": []
            }
            """);

        var watcher = NewWatcher(NewGraph(), NewArm());
        watcher.Start(new CancellationToken(canceled: true));
        await watcher.StopAsync();

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);

        var entra = Assert.Single(watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraId);
        Assert.Equal("elig-direct", entra.EligibilityId);
        // The known cap survives the row that had none.
        Assert.Equal(TimeSpan.FromHours(2), entra.MaxActivationDuration);

        var armRow = Assert.Single(watcher.CurrentEligibleRoles, r => r.Source == PimSource.AzureRbac);
        Assert.Equal(TimeSpan.FromHours(8), armRow.MaxActivationDuration);
    }

    // Distinct scopes in a cache file are still distinct after hydration.
    [Fact]
    public async Task Start_CacheWithTwoScopesForOneRole_HydratesBothRows()
    {
        WriteCache("""
            {
              "Roles": [
                {
                  "Source": 1,
                  "RoleName": "Reader",
                  "RoleDefinitionId": "role-reader",
                  "ScopeDisplay": "Sub 1",
                  "ArmScope": "/subscriptions/sub-1",
                  "EligibilityId": "elig-1"
                },
                {
                  "Source": 1,
                  "RoleName": "Reader",
                  "RoleDefinitionId": "role-reader",
                  "ScopeDisplay": "Sub 2",
                  "ArmScope": "/subscriptions/sub-2",
                  "EligibilityId": "elig-2"
                }
              ],
              "RelevantSubscriptionIds": []
            }
            """);

        var watcher = NewWatcher(NewGraph(), NewArm());
        watcher.Start(new CancellationToken(canceled: true));
        await watcher.StopAsync();

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
    }

    // ---- helpers ----------------------------------------------------------

    private void WriteCache(string json)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(Path.Combine(_dataDir, $"eligible-roles-{Tenant.TenantId}.json"), json);
    }

    private static Dictionary<string, RolePolicy> EntraPolicies(
        params (string RoleDefinitionId, TimeSpan Cap)[] entries)
    {
        var dict = new Dictionary<string, RolePolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var (roleDefinitionId, cap) in entries)
        {
            dict[roleDefinitionId] = new RolePolicy(ApprovalRequired: null, MaxActivationDuration: cap);
        }
        return dict;
    }

    private static Dictionary<ArmRolePolicyKey, RolePolicy> ArmPolicies(
        params (string Scope, string RoleDefinitionId, TimeSpan Cap)[] entries)
    {
        var dict = new Dictionary<ArmRolePolicyKey, RolePolicy>();
        foreach (var (scope, roleDefinitionId, cap) in entries)
        {
            dict[ArmRolePolicyKey.For(scope, roleDefinitionId)] =
                new RolePolicy(ApprovalRequired: null, MaxActivationDuration: cap);
        }
        return dict;
    }

    // ---- builders ---------------------------------------------------------

    private static EntraEligibilitySchedule GraphEligible(
        string roleDisplayName,
        string roleDefId,
        string? directoryScopeId = "/",
        string? memberType = "Direct",
        string eligibilityId = "elig-1")
        => new(
            Id: eligibilityId,
            PrincipalId: "prin-1",
            RoleDefinitionId: roleDefId,
            DirectoryScopeId: directoryScopeId,
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: null,
            MemberType: memberType,
            Principal: new EntraPrincipal("prin-1", "Alice", null),
            RoleDefinition: new EntraRoleDefinition(roleDefId, roleDisplayName, null));

    private static ArmSubscription ArmSub(string id)
        => new($"/subscriptions/{id}", id, $"Sub {id}", "Enabled");

    private static ArmEligibilitySchedule ArmEligible(string roleDisplayName, string roleDefId, string scope)
        => new(
            Id: $"{scope}/providers/Microsoft.Authorization/roleEligibilitySchedules/elig-{roleDefId}",
            Name: $"elig-{roleDefId}",
            Properties: new ArmEligibilityProperties(
                PrincipalId: "prin-1",
                RoleDefinitionId: roleDefId,
                Scope: scope,
                Status: "Active",
                MemberType: "Direct",
                StartDateTime: DateTimeOffset.UtcNow,
                EndDateTime: null,
                ExpandedProperties: new ArmExpandedProperties(
                    Principal: new ArmPrincipalDto("prin-1", "Alice", "User", null),
                    RoleDefinition: new ArmRoleDefinitionDto(roleDefId, roleDisplayName, null),
                    Scope: new ArmScopeDto(scope, "Prod MG", "managementgroup"))));

    private static ArmRoleAssignmentScheduleInstance ArmActive(
        string roleDisplayName, string roleDefId, string scope, DateTimeOffset? endDateTime)
        => new(
            Id: $"{scope}/providers/Microsoft.Authorization/roleAssignmentScheduleInstances/inst-{roleDefId}",
            Name: $"inst-{roleDefId}",
            Properties: new ArmRoleAssignmentInstanceProperties(
                PrincipalId: "prin-1",
                RoleDefinitionId: roleDefId,
                Scope: scope,
                Status: "Provisioned",
                AssignmentType: "Activated",
                MemberType: "Direct",
                StartDateTime: DateTimeOffset.UtcNow,
                EndDateTime: endDateTime,
                ExpandedProperties: new ArmExpandedProperties(
                    Principal: new ArmPrincipalDto("prin-1", "Alice", "User", null),
                    RoleDefinition: new ArmRoleDefinitionDto(roleDefId, roleDisplayName, null),
                    Scope: new ArmScopeDto(scope, "Prod MG", "managementgroup"))));

    private static IGraphPimClient NewGraph(IReadOnlyList<EntraEligibilitySchedule>? eligible = null)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(eligible ?? Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.GetRolePoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(EntraPolicies());
        return graph;
    }

    private static IArmPimClient NewArm(
        IReadOnlyList<ArmSubscription>? subscriptions = null,
        IReadOnlyList<ArmEligibilitySchedule>? eligible = null,
        IReadOnlyList<ArmRoleAssignmentScheduleInstance>? actives = null)
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(subscriptions ?? Array.Empty<ArmSubscription>());
        arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(eligible ?? Array.Empty<ArmEligibilitySchedule>());
        arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(actives ?? Array.Empty<ArmRoleAssignmentScheduleInstance>());
        arm.GetRolePoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ArmPolicies());
        return arm;
    }

    private EligibleRolesWatcher NewWatcher(IGraphPimClient graph, IArmPimClient arm)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherDedupTests>.Instance);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.DataDir.Returns(_dataDir);
        ctx.Tenants.Returns(new List<PluginTenant> { Tenant });

        return new EligibleRolesWatcher(
            graph, arm, ctx, Tenant,
            TimeSpan.FromMilliseconds(50),
            new PendingActivationStore(ctx, Tenant));
    }
}
