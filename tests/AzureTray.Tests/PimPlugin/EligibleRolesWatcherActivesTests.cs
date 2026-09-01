using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Eligibility and active assignments come from the same subscription
// enumeration, but a failure in the actives leg must not cost the user their
// eligible-role list.
public sealed class EligibleRolesWatcherActivesTests
{
    [Fact]
    public async Task PollAsync_ArmActivesFetchFails_StillListsArmEligibleRoles()
    {
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            eligible: new[] { ArmEligible("Contributor", "arm-role-contrib", "/subscriptions/sub-1") });
        arm.ListActiveRoleAssignmentsAsync(
                Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<ArmRoleAssignmentScheduleInstance>>(
                _ => throw new HttpRequestException("ARM 500"));

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        // The failing leg really ran, and the eligibility leg survived it.
        await arm.Received(1).ListActiveRoleAssignmentsAsync(
            "prin-1", Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
        Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal(PimSource.AzureRbac, watcher.CurrentEligibleRoles[0].Source);
        Assert.Equal("Contributor", watcher.CurrentEligibleRoles[0].RoleName);
        // Actives unknown this cycle — the row lists, it just isn't grayed out.
        Assert.Empty(watcher.CurrentActiveAssignments);
        Assert.Null(watcher.FindActiveFor(watcher.CurrentEligibleRoles[0]));
    }

    [Fact]
    public async Task PollAsync_GraphActivesFetchFails_StillListsGraphEligibleRoles()
    {
        var graph = NewGraph(eligible: new[] { GraphSchedule("Owner", "role-owner", null) });
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<EntraEligibilitySchedule>>(
                _ => throw new HttpRequestException("Graph 500"));
        var arm = NewArm();

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        await graph.Received(1).ListActiveRoleAssignmentsAsync(
            "prin-1", Arg.Any<CancellationToken>());
        Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Empty(watcher.CurrentActiveAssignments);
    }

    // A Graph failure must not take the ARM actives with it (and vice versa).
    [Fact]
    public async Task PollAsync_GraphActivesFail_ArmActivesStillLoad()
    {
        var graph = NewGraph(eligible: new[] { GraphSchedule("Owner", "role-owner", null) });
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<EntraEligibilitySchedule>>(
                _ => throw new HttpRequestException("Graph 500"));
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            eligible: new[] { ArmEligible("Contributor", "arm-role-contrib", "/subscriptions/sub-1") },
            actives: new[] { ArmActive("Contributor", "arm-role-contrib", "/subscriptions/sub-1", DateTimeOffset.UtcNow.AddHours(1)) });

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Single(watcher.CurrentActiveAssignments);
        Assert.Equal(PimSource.AzureRbac, watcher.CurrentActiveAssignments[0].Source);
    }

    [Fact]
    public async Task PollAsync_ArmActive_AtSubscriptionScope_MatchesResourceGroupRow()
    {
        var end = DateTimeOffset.UtcNow.AddHours(3);
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            eligible: new[]
            {
                ArmEligible("Contributor", "arm-role-contrib", "/subscriptions/sub-1/resourceGroups/rg-1"),
            },
            actives: new[] { ArmActive("Contributor", "arm-role-contrib", "/subscriptions/sub-1", end) });

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        var active = watcher.FindActiveFor(watcher.CurrentEligibleRoles[0]);
        Assert.NotNull(active);
        Assert.Equal(end, active!.EndDateTime);
    }

    [Fact]
    public async Task PollAsync_ArmActive_DoesNotMarkAnEntraRowActive_OnNameCollision()
    {
        var graph = NewGraph(eligible: new[] { GraphSchedule("Owner", "entra-role-owner", null) });
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            eligible: new[] { ArmEligible("Owner", "arm-role-owner", "/subscriptions/sub-1") },
            actives: new[] { ArmActive("Owner", "arm-role-owner", "/subscriptions/sub-1", DateTimeOffset.UtcNow.AddHours(1)) });

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        var entraRow = Assert.Single(watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraId);
        var armRow = Assert.Single(watcher.CurrentEligibleRoles, r => r.Source == PimSource.AzureRbac);

        Assert.Null(watcher.FindActiveFor(entraRow));
        Assert.NotNull(watcher.FindActiveFor(armRow));
    }

    [Fact]
    public async Task PollAsync_ArmActiveWithoutProperties_IsSkipped()
    {
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            eligible: new[] { ArmEligible("Contributor", "arm-role-contrib", "/subscriptions/sub-1") },
            actives: new[] { new ArmRoleAssignmentScheduleInstance("/.../inst-1", "inst-1", null) });

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(watcher.CurrentActiveAssignments);
        Assert.Single(watcher.CurrentEligibleRoles);
    }

    [Fact]
    public async Task PollAsync_NoSubscriptions_SkipsTheArmActivesRead()
    {
        var graph = NewGraph();
        var arm = NewArm();

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        await arm.DidNotReceive().ListActiveRoleAssignmentsAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>());
    }

    // ---- builders ---------------------------------------------------------

    private static EntraEligibilitySchedule GraphSchedule(
        string roleDisplayName, string roleDefId, DateTimeOffset? endDateTime)
        => new(
            Id: $"elig-{roleDefId}",
            PrincipalId: "prin-1",
            RoleDefinitionId: roleDefId,
            DirectoryScopeId: "/",
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: endDateTime,
            MemberType: "Direct",
            Principal: new EntraPrincipal("prin-1", "Alice", null),
            RoleDefinition: new EntraRoleDefinition(roleDefId, roleDisplayName, null));

    private static ArmSubscription ArmSub(string id, string displayName)
        => new($"/subscriptions/{id}", id, displayName, "Enabled");

    private static ArmEligibilitySchedule ArmEligible(string roleDisplayName, string roleDefId, string scope)
        => new(
            Id: $"/.../eligibility-{roleDefId}",
            Name: $"eligibility-{roleDefId}",
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
                    Scope: new ArmScopeDto(scope, "Dev (sub)", "subscription"))));

    private static ArmRoleAssignmentScheduleInstance ArmActive(
        string roleDisplayName, string roleDefId, string scope, DateTimeOffset? endDateTime)
        => new(
            Id: $"/.../instance-{roleDefId}",
            Name: $"instance-{roleDefId}",
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
                    Scope: new ArmScopeDto(scope, "Dev (sub)", "subscription"))));

    private static IGraphPimClient NewGraph(IReadOnlyList<EntraEligibilitySchedule>? eligible = null)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(eligible ?? Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
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
        return arm;
    }

    private static EligibleRolesWatcher NewWatcher(IGraphPimClient graph, IArmPimClient arm)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherActivesTests>.Instance);
        ctx.Notifier.Returns(Substitute.For<INotifier>());

        return new EligibleRolesWatcher(
            graph, arm, Substitute.For<IGraphGroupPimClient>(), ctx, new PluginTenant("tenant-1", "Contoso"),
            TimeSpan.FromMilliseconds(50),
            new PendingActivationStore(ctx, new PluginTenant("tenant-1", "Contoso")));
    }
}
