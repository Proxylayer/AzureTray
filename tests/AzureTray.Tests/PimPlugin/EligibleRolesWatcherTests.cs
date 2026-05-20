using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

public sealed class EligibleRolesWatcherTests
{
    [Fact]
    public async Task PollAsync_PopulatesActiveRoleNames_FromGraph()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(new[] { GraphEligible("Owner", "graph-role-owner") });
        graph.ListActiveRoleAssignmentsAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                GraphEligible("Owner", "graph-role-owner"),
                GraphEligible("Reader", "graph-role-reader"),
            });

        var arm = NewArm();
        var watcher = NewWatcher(graph, arm);

        Assert.Empty(watcher.CurrentActiveRoleNames);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Contains("Owner", watcher.CurrentActiveRoleNames);
        Assert.Contains("Reader", watcher.CurrentActiveRoleNames);
        Assert.Equal(2, watcher.CurrentActiveRoleNames.Count);
    }

    [Fact]
    public async Task PollAsync_ActiveRoleLookupIsCaseInsensitive()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { GraphEligible("Owner", "graph-role-owner") });

        var arm = NewArm();
        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Contains("owner", watcher.CurrentActiveRoleNames);
        Assert.Contains("OWNER", watcher.CurrentActiveRoleNames);
    }

    [Fact]
    public async Task PollAsync_NoPrincipal_ProducesEmptySnapshot()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        var arm = NewArm();
        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(watcher.CurrentEligibleRoles);
        await graph.DidNotReceive().ListEligibleRolesAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_PopulatesSnapshotFromBothSources()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync("prin-1", Arg.Any<CancellationToken>())
            .Returns(new[] { GraphEligible("Owner", "graph-role-owner") });

        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ArmSub("sub-1", "Dev") });
        arm.ListEligibleRolesAsync(
            "prin-1",
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>())
            .Returns(new[] { ArmEligible("Contributor", "arm-role-contributor", "/subscriptions/sub-1") });

        var watcher = NewWatcher(graph, arm);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
        Assert.Contains(watcher.CurrentEligibleRoles, r =>
            r.Source == PimSource.EntraId && r.RoleName == "Owner" && r.RoleDefinitionId == "graph-role-owner");
        Assert.Contains(watcher.CurrentEligibleRoles, r =>
            r.Source == PimSource.AzureRbac && r.RoleName == "Contributor" && r.ArmScope == "/subscriptions/sub-1");
    }

    [Fact]
    public async Task HandleActivationAsync_Entra_PromptsAndCallsGraphActivate()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        var arm = NewArm();
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("4 hours", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("operations"));

        var watcher = NewWatcher(graph, arm, notifier);
        await watcher.PollAsync(CancellationToken.None); // resolve principal id

        var role = new UnifiedEligibleRole(
            Source: PimSource.EntraId,
            RoleName: "Owner",
            RoleDefinitionId: "graph-role-owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            EligibilityId: "elig-1");

        await watcher.HandleActivationAsync(role, CancellationToken.None);

        await graph.Received(1).ActivateRoleAsync(
            "prin-1",
            "graph-role-owner",
            TimeSpan.FromHours(4),
            "operations",
            Arg.Any<CancellationToken>());
        await arm.DidNotReceive().ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleActivationAsync_Arm_PromptsAndCallsArmActivateWithScopeAndEligibilityId()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        var arm = NewArm();
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("1 hour", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("incident #42"));

        var watcher = NewWatcher(graph, arm, notifier);
        await watcher.PollAsync(CancellationToken.None);

        var role = new UnifiedEligibleRole(
            Source: PimSource.AzureRbac,
            RoleName: "Contributor",
            RoleDefinitionId: "arm-role-contributor",
            ScopeDisplay: "Dev (sub)",
            ArmScope: "/subscriptions/sub-1",
            EligibilityId: "elig-arm-1");

        await watcher.HandleActivationAsync(role, CancellationToken.None);

        await arm.Received(1).ActivateRoleAsync(
            "/subscriptions/sub-1",
            "prin-1",
            "arm-role-contributor",
            "elig-arm-1",
            TimeSpan.FromHours(1),
            "incident #42",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleActivationAsync_DismissedAtDuration_DoesNotActivate()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        var arm = NewArm();
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DismissedResult());

        var watcher = NewWatcher(graph, arm, notifier);
        await watcher.PollAsync(CancellationToken.None);

        var role = new UnifiedEligibleRole(
            Source: PimSource.EntraId,
            RoleName: "Owner",
            RoleDefinitionId: "graph-role-owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            EligibilityId: null);

        await watcher.HandleActivationAsync(role, CancellationToken.None);

        await graph.DidNotReceive().ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleActivationAsync_BlankJustification_DoesNotActivate()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        var arm = NewArm();
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("1 hour", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("   "));

        var watcher = NewWatcher(graph, arm, notifier);
        await watcher.PollAsync(CancellationToken.None);

        var role = new UnifiedEligibleRole(
            Source: PimSource.EntraId,
            RoleName: "Owner",
            RoleDefinitionId: "graph-role-owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            EligibilityId: null);

        await watcher.HandleActivationAsync(role, CancellationToken.None);

        await graph.DidNotReceive().ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- builders ---------------------------------------------------------

    private static EntraEligibilitySchedule GraphEligible(string roleDisplayName, string roleDefId)
        => new(
            Id: $"elig-{roleDefId}",
            PrincipalId: "prin-1",
            RoleDefinitionId: roleDefId,
            DirectoryScopeId: "/",
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: null,
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

    private static IArmPimClient NewArm(
        IReadOnlyList<ArmSubscription>? subscriptions = null,
        IReadOnlyList<ArmEligibilitySchedule>? roles = null)
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(subscriptions ?? Array.Empty<ArmSubscription>());
        arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(roles ?? Array.Empty<ArmEligibilitySchedule>());
        return arm;
    }

    private static EligibleRolesWatcher NewWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        INotifier? notifier = null)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcher>.Instance);
        ctx.Notifier.Returns(notifier ?? Substitute.For<INotifier>());
        ctx.Tenants.Returns(new List<PluginTenant> { new("tenant-1", "Contoso") });

        return new EligibleRolesWatcher(
            graph, arm, ctx,
            new PluginTenant("tenant-1", "Contoso"),
            TimeSpan.FromMilliseconds(50));
    }
}
