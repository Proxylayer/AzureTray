using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;
using AzureTray.Plugin.PIM.Groups.Dto;
using AzureTray.Plugin.PIM.Policies;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// PIM for Groups as the watcher's third source. The risk this file covers is
// not the group rows themselves but the blast radius: three sources are polled
// concurrently into one snapshot, and the existing per-source degradation tests
// exist because a single failing feed used to blank the menu. Adding a source
// re-opens that question in both directions, so a group failure must leave
// Entra and ARM standing and an Entra or ARM failure must leave the group rows
// standing.
//
// The cache is the other seam: GroupId was appended to UnifiedEligibleRole and
// ActiveRoleAssignment as a trailing optional member precisely so a cache file
// written before PIM for Groups existed still deserializes. That is pinned
// explicitly — a regression there is a startup exception on every upgrade, and
// it would only show up on machines that had run the previous version.
public sealed class EligibleRolesWatcherGroupTests : IDisposable
{
    private readonly string _dataDir = Path.Combine(
        Path.GetTempPath(), "azuretray-tests", Guid.NewGuid().ToString("N"));

    private static readonly PluginTenant Tenant = new("tenant-1", "Contoso");

    public void Dispose()
    {
        try { if (Directory.Exists(_dataDir)) Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- snapshot ---------------------------------------------------------

    [Fact]
    public async Task PollAsync_GroupRowsAppearInTheSnapshot_AlongsideEntraAndArm()
    {
        var watcher = NewWatcher(
            NewGraph(eligible: true),
            NewArm(eligible: true),
            NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins")));

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(3, watcher.CurrentEligibleRoles.Count);
        var group = Assert.Single(
            watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraGroup);

        // The access id fills the role slot and the group name the scope slot,
        // so the row reads "Member (Contoso SQL Admins)".
        Assert.Equal("Member", group.RoleName);
        Assert.Equal("member", group.RoleDefinitionId);
        Assert.Equal("Contoso SQL Admins", group.ScopeDisplay);
        Assert.Equal("group-1", group.GroupId);
        Assert.Null(group.ArmScope);
        Assert.Null(group.DirectoryScopeId);
    }

    // The group's display name is guaranteed by the client, but a row whose
    // name never resolved still has to render something.
    [Fact]
    public async Task PollAsync_GroupWithoutADisplayName_FallsBackToTheGroupId()
    {
        var watcher = NewWatcher(NewGraph(), NewArm(), NewGroups(
            new GroupEligibilityScheduleInstance(
                Id: "elig-g1", PrincipalId: "prin-1", AccessId: "owner", GroupId: "group-1",
                MemberType: "Direct", EligibilityScheduleId: null,
                StartDateTime: null, EndDateTime: null, Group: null)));

        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("group-1", row.ScopeDisplay);
        Assert.Equal("Owner", row.RoleName);
    }

    // An eligibility with no group id has no scope to act on, so it is dropped
    // rather than rendered as an unusable row.
    [Fact]
    public async Task PollAsync_EligibilityWithoutAGroupId_IsDropped()
    {
        var watcher = NewWatcher(NewGraph(), NewArm(), NewGroups(
            new GroupEligibilityScheduleInstance(
                Id: "elig-g1", PrincipalId: "prin-1", AccessId: "member", GroupId: null,
                MemberType: "Direct", EligibilityScheduleId: null,
                StartDateTime: null, EndDateTime: null, Group: null)));

        await watcher.PollAsync(CancellationToken.None);

        Assert.Empty(watcher.CurrentEligibleRoles);
    }

    // ---- per-source degradation -------------------------------------------

    // The whole point of the separate try/catch per source: a tenant that has
    // not consented the PIM for Groups scopes still gets its Entra and ARM rows.
    [Fact]
    public async Task PollAsync_GroupFetchFails_LeavesTheEntraAndArmRowsIntact()
    {
        var groups = Substitute.For<IGraphGroupPimClient>();
        groups.ListEligibleGroupsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("403"));

        var watcher = NewWatcher(NewGraph(eligible: true), NewArm(eligible: true), groups);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraId);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.Source == PimSource.AzureRbac);
    }

    [Fact]
    public async Task PollAsync_EntraFetchFails_LeavesTheGroupRowsIntact()
    {
        var graph = NewGraph();
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("403"));

        var watcher = NewWatcher(
            graph, NewArm(eligible: true), NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins")));

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraGroup);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.Source == PimSource.AzureRbac);
    }

    [Fact]
    public async Task PollAsync_ArmFetchFails_LeavesTheGroupRowsIntact()
    {
        var arm = NewArm();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("no ARM"));

        var watcher = NewWatcher(
            NewGraph(eligible: true), arm, NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins")));

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(2, watcher.CurrentEligibleRoles.Count);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraGroup);
        Assert.Contains(watcher.CurrentEligibleRoles, r => r.Source == PimSource.EntraId);
    }

    // The active read is caught separately from the eligibility read, so losing
    // it costs the grey-out and nothing else.
    [Fact]
    public async Task PollAsync_GroupActiveFetchFails_StillListsTheGroupRows()
    {
        var groups = NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins"));
        groups.ListActiveGroupAssignmentsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("boom"));

        var watcher = NewWatcher(NewGraph(), NewArm(), groups);

        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Null(watcher.FindActiveFor(row));
    }

    // ---- actives ----------------------------------------------------------

    [Fact]
    public async Task PollAsync_ActiveGroupAssignment_MatchesItsEligibleRow_AndCarriesTheEndTime()
    {
        var end = DateTimeOffset.UtcNow.AddHours(3);
        var groups = NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins"));
        groups.ListActiveGroupAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { GroupActive("group-1", "Member", end) });

        var watcher = NewWatcher(NewGraph(), NewArm(), groups);

        await watcher.PollAsync(CancellationToken.None);

        var active = watcher.FindActiveFor(Assert.Single(watcher.CurrentEligibleRoles));
        Assert.NotNull(active);
        Assert.Equal(end, active!.EndDateTime);
        Assert.Equal(PimSource.EntraGroup, active.Source);
    }

    // A standing (permanent) assignment has no end time. It still grays the row
    // out — null means permanent, not "unknown".
    [Fact]
    public async Task PollAsync_PermanentGroupAssignment_StillMarksTheRowActive()
    {
        var groups = NewGroups(GroupEligible("group-1", "owner", "Contoso SQL Admins"));
        groups.ListActiveGroupAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { GroupActive("group-1", "owner", endDateTime: null) });

        var watcher = NewWatcher(NewGraph(), NewArm(), groups);

        await watcher.PollAsync(CancellationToken.None);

        var active = watcher.FindActiveFor(Assert.Single(watcher.CurrentEligibleRoles));
        Assert.NotNull(active);
        Assert.Null(active!.EndDateTime);
    }

    // An assignment on another group must not gray this row out, even though
    // every group row shares the same two access ids.
    [Fact]
    public async Task PollAsync_ActiveAssignmentOnAnotherGroup_DoesNotMarkThisRowActive()
    {
        var groups = NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins"));
        groups.ListActiveGroupAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { GroupActive("group-2", "member", DateTimeOffset.UtcNow.AddHours(1)) });

        var watcher = NewWatcher(NewGraph(), NewArm(), groups);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Null(watcher.FindActiveFor(Assert.Single(watcher.CurrentEligibleRoles)));
    }

    // ---- caps -------------------------------------------------------------

    [Fact]
    public async Task PollAsync_AttachesTheGroupPolicyCap_PerAccessId()
    {
        var groups = NewGroups(
            GroupEligible("group-1", "member", "Contoso SQL Admins"),
            GroupEligible("group-1", "owner", "Contoso SQL Admins"));
        groups.GetGroupPoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<GroupRolePolicyKey, RolePolicy>
            {
                [GroupRolePolicyKey.For("group-1", "member")] = new(ApprovalRequired: false, TimeSpan.FromHours(2)),
                [GroupRolePolicyKey.For("group-1", "owner")] = new(ApprovalRequired: true, TimeSpan.FromHours(1)),
            });

        var watcher = NewWatcher(NewGraph(), NewArm(), groups);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(
            TimeSpan.FromHours(2),
            Single(watcher, "member").MaxActivationDuration);
        Assert.Equal(
            TimeSpan.FromHours(1),
            Single(watcher, "owner").MaxActivationDuration);
    }

    // A policy read that fails is "cap unknown", never "cap unrestricted" — the
    // rows survive without one and the prompt falls back to the 8h ceiling.
    [Fact]
    public async Task PollAsync_GroupPolicyReadFails_KeepsTheRowsWithAnUnknownCap()
    {
        var groups = NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins"));
        groups.GetGroupPoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("403"));

        var watcher = NewWatcher(NewGraph(), NewArm(), groups);

        await watcher.PollAsync(CancellationToken.None);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Null(row.MaxActivationDuration);
    }

    // ---- cache ------------------------------------------------------------

    [Fact]
    public async Task PollAsync_GroupRowsRoundTripThroughTheDiskCache()
    {
        var end = DateTimeOffset.UtcNow.AddHours(2);
        var groups = NewGroups(GroupEligible("group-1", "member", "Contoso SQL Admins"));
        groups.ListActiveGroupAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { GroupActive("group-1", "member", end) });

        var writer = NewWatcher(NewGraph(), NewArm(), groups);
        await writer.PollAsync(CancellationToken.None);

        var reader = NewWatcher(NewGraph(), NewArm(), NewGroups());
        await HydrateAsync(reader);

        var row = Assert.Single(reader.CurrentEligibleRoles);
        Assert.Equal(PimSource.EntraGroup, row.Source);
        Assert.Equal("group-1", row.GroupId);
        Assert.Equal("Contoso SQL Admins", row.ScopeDisplay);

        // The active assignment's group id survives too, or the restored row
        // would come back ungreyed until the first poll lands.
        var active = reader.FindActiveFor(row);
        Assert.NotNull(active);
        Assert.Equal(end, active!.EndDateTime);
    }

    // Back-compat. A cache written by the previous version has no GroupId on
    // either record — the member was appended as a trailing optional so those
    // files keep loading. A regression here throws on startup for every user
    // who upgrades with a warm cache.
    [Fact]
    public async Task Start_CacheWrittenBeforeGroupIdExisted_StillDeserializes()
    {
        WriteCache("""
            {
              "Roles": [
                {
                  "Source": 0,
                  "RoleName": "Owner",
                  "RoleDefinitionId": "role-owner",
                  "ScopeDisplay": "Entra ID directory",
                  "ArmScope": null,
                  "EligibilityId": "elig-1",
                  "MaxActivationDuration": null,
                  "MemberType": "Direct",
                  "DirectoryScopeId": "/"
                }
              ],
              "ActiveAssignments": [
                {
                  "Source": 0,
                  "RoleName": "Owner",
                  "RoleDefinitionId": "role-owner",
                  "Scope": "/",
                  "EndDateTime": null
                }
              ],
              "RelevantSubscriptionIds": []
            }
            """);

        var watcher = NewWatcher(NewGraph(), NewArm(), NewGroups());
        await HydrateAsync(watcher);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal("Owner", row.RoleName);
        // Absent in the file, so it reads as "not a group row".
        Assert.Null(row.GroupId);

        var active = Assert.Single(watcher.CurrentActiveAssignments);
        Assert.Null(active.GroupId);
        Assert.NotNull(watcher.FindActiveFor(row));
    }

    // The forward half of the same contract: a cache file that DOES carry group
    // rows loads them, so an upgrade does not silently drop the new source
    // until the first poll.
    [Fact]
    public async Task Start_CacheWithGroupRows_LoadsThem()
    {
        WriteCache("""
            {
              "Roles": [
                {
                  "Source": 2,
                  "RoleName": "Member",
                  "RoleDefinitionId": "member",
                  "ScopeDisplay": "Contoso SQL Admins",
                  "ArmScope": null,
                  "EligibilityId": "elig-g1",
                  "MaxActivationDuration": null,
                  "MemberType": "Direct",
                  "DirectoryScopeId": null,
                  "GroupId": "group-1"
                }
              ],
              "ActiveAssignments": [
                {
                  "Source": 2,
                  "RoleName": "Member",
                  "RoleDefinitionId": "member",
                  "Scope": null,
                  "EndDateTime": null,
                  "GroupId": "group-1"
                }
              ],
              "RelevantSubscriptionIds": []
            }
            """);

        var watcher = NewWatcher(NewGraph(), NewArm(), NewGroups());
        await HydrateAsync(watcher);

        var row = Assert.Single(watcher.CurrentEligibleRoles);
        Assert.Equal(PimSource.EntraGroup, row.Source);
        Assert.Equal("group-1", row.GroupId);
        Assert.NotNull(watcher.FindActiveFor(row));
    }

    // ---- activation dispatch ----------------------------------------------

    [Fact]
    public async Task HandleActivationAsync_Group_DispatchesToTheGroupClient_WithTheGroupAndAccessId()
    {
        var groups = NewGroups();
        groups.ActivateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GroupScheduleRequest(
                Id: "req-g1", Status: "Provisioned", Action: "selfActivate", AccessId: "member",
                PrincipalId: "prin-1", GroupId: "group-1", Justification: "on call", ApprovalId: null,
                TargetScheduleId: null, CreatedDateTime: null, CompletedDateTime: null,
                Principal: null, Group: null));

        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("4 hours", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("on call"));

        var graph = NewGraph();
        var arm = NewArm();
        var watcher = NewWatcher(graph, arm, groups, notifier);
        await watcher.PollAsync(CancellationToken.None); // resolve the principal id

        await watcher.HandleActivationAsync(GroupRow("group-1", "member"), CancellationToken.None);

        await groups.Received(1).ActivateAsync(
            "prin-1", "group-1", "member", TimeSpan.FromHours(4), "on call", Arg.Any<CancellationToken>());
        await graph.DidNotReceive().ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Only reachable from a hand-edited cache file, but the row must refuse
    // rather than post an activation with an empty group id.
    [Fact]
    public async Task HandleActivationAsync_GroupRowWithoutAGroupId_DoesNotCallGraph()
    {
        var groups = NewGroups();
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("1 hour", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("why"));

        var watcher = NewWatcher(NewGraph(), NewArm(), groups, notifier);
        await watcher.PollAsync(CancellationToken.None);

        await watcher.HandleActivationAsync(
            GroupRow(groupId: null, "member"), CancellationToken.None);

        await groups.DidNotReceive().ActivateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleDeactivationAsync_Group_DispatchesToTheGroupClient()
    {
        var groups = NewGroups();
        groups.DeactivateAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new GroupScheduleRequest(
                Id: "req-g2", Status: "Provisioned", Action: "selfDeactivate", AccessId: "owner",
                PrincipalId: "prin-1", GroupId: "group-1", Justification: null, ApprovalId: null,
                TargetScheduleId: null, CreatedDateTime: null, CompletedDateTime: null,
                Principal: null, Group: null));

        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<YesNoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new YesNoResult(true));

        var arm = NewArm();
        var watcher = NewWatcher(NewGraph(), arm, groups, notifier);
        await watcher.PollAsync(CancellationToken.None);

        await watcher.HandleDeactivationAsync(GroupRow("group-1", "owner"), CancellationToken.None);

        await groups.Received(1).DeactivateAsync(
            "prin-1", "group-1", "owner", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await arm.DidNotReceive().DeactivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ---- helpers ----------------------------------------------------------

    private static UnifiedEligibleRole Single(EligibleRolesWatcher watcher, string accessId)
        => watcher.CurrentEligibleRoles.Single(r =>
            r.Source == PimSource.EntraGroup && r.RoleDefinitionId == accessId);

    private static UnifiedEligibleRole GroupRow(string? groupId, string accessId)
        => new(
            Source: PimSource.EntraGroup,
            RoleName: accessId == "owner" ? "Owner" : "Member",
            RoleDefinitionId: accessId,
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            EligibilityId: "elig-g1",
            GroupId: groupId);

    private static GroupEligibilityScheduleInstance GroupEligible(
        string groupId, string accessId, string displayName)
        => new(
            Id: $"elig-{groupId}-{accessId}",
            PrincipalId: "prin-1",
            AccessId: accessId,
            GroupId: groupId,
            MemberType: "Direct",
            EligibilityScheduleId: null,
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: null,
            Group: new GroupRef(groupId, displayName));

    private static GroupAssignmentScheduleInstance GroupActive(
        string groupId, string accessId, DateTimeOffset? endDateTime)
        => new(
            Id: $"inst-{groupId}-{accessId}",
            PrincipalId: "prin-1",
            AccessId: accessId,
            GroupId: groupId,
            MemberType: "Direct",
            AssignmentScheduleId: null,
            AssignmentType: "Activated",
            StartDateTime: DateTimeOffset.UtcNow,
            EndDateTime: endDateTime);

    private static IGraphGroupPimClient NewGroups(params GroupEligibilityScheduleInstance[] eligible)
    {
        var groups = Substitute.For<IGraphGroupPimClient>();
        groups.ListEligibleGroupsAsync(Arg.Any<CancellationToken>()).Returns(eligible);
        groups.ListActiveGroupAssignmentsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<GroupAssignmentScheduleInstance>());
        groups.GetGroupPoliciesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<GroupRolePolicyKey, RolePolicy>());
        return groups;
    }

    private static IGraphPimClient NewGraph(bool eligible = false)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(eligible
                ? new[]
                {
                    new EntraEligibilitySchedule(
                        Id: "elig-1", PrincipalId: "prin-1", RoleDefinitionId: "role-owner",
                        DirectoryScopeId: "/", StartDateTime: DateTimeOffset.UtcNow, EndDateTime: null,
                        MemberType: "Direct", Principal: null,
                        RoleDefinition: new EntraRoleDefinition("role-owner", "Owner", null)),
                }
                : Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        return graph;
    }

    private static IArmPimClient NewArm(bool eligible = false)
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(eligible
                ? new[] { new ArmSubscription("/subscriptions/sub-1", "sub-1", "Dev sub", "Enabled") }
                : Array.Empty<ArmSubscription>());
        arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(eligible
                ? new[]
                {
                    new ArmEligibilitySchedule(
                        Id: "/.../elig-arm-1",
                        Name: "elig-arm-1",
                        Properties: new ArmEligibilityProperties(
                            PrincipalId: "prin-1",
                            RoleDefinitionId: "arm-role-reader",
                            Scope: "/subscriptions/sub-1",
                            Status: "Active",
                            MemberType: "Direct",
                            StartDateTime: DateTimeOffset.UtcNow,
                            EndDateTime: null,
                            ExpandedProperties: new ArmExpandedProperties(
                                Principal: new ArmPrincipalDto("prin-1", "Alice", "User", null),
                                RoleDefinition: new ArmRoleDefinitionDto("arm-role-reader", "Reader", null),
                                Scope: new ArmScopeDto("/subscriptions/sub-1", "Dev sub", "subscription")))),
                }
                : Array.Empty<ArmEligibilitySchedule>());
        arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmRoleAssignmentScheduleInstance>());
        return arm;
    }

    private string CachePath => Path.Combine(_dataDir, $"eligible-roles-{Tenant.TenantId}.json");

    private void WriteCache(string json)
    {
        Directory.CreateDirectory(_dataDir);
        File.WriteAllText(CachePath, json);
    }

    // Start() hydrates from cache synchronously, then queues the poll loop on
    // the supplied token — a cancelled one means the loop body never runs.
    private static async Task HydrateAsync(EligibleRolesWatcher watcher)
    {
        watcher.Start(new CancellationToken(canceled: true));
        await watcher.StopAsync();
    }

    private EligibleRolesWatcher NewWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IGraphGroupPimClient groups,
        INotifier? notifier = null)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherGroupTests>.Instance);
        ctx.Notifier.Returns(notifier ?? Substitute.For<INotifier>());
        ctx.Tenants.Returns(new List<PluginTenant> { Tenant });
        ctx.DataDir.Returns(_dataDir);

        return new EligibleRolesWatcher(
            graph, arm, groups, ctx, Tenant,
            TimeSpan.FromMilliseconds(50),
            new PendingActivationStore(ctx, Tenant));
    }
}
