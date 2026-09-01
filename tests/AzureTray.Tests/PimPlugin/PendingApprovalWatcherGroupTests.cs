using System;
using System.Collections.Generic;
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
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// PIM for Groups as the approver feed's third source. Same blast-radius concern
// as the eligible-roles watcher — three feeds concatenate into one approval
// list, so a failure in any one of them must not silence the others — plus one
// group-specific outcome: a stage lists several approvers and the first
// decision closes it, so a losing PATCH comes back 409. The client turns that
// into ApprovalAlreadyDecidedException, and the watcher owes the user an
// explanation rather than an error they cannot act on.
public sealed class PendingApprovalWatcherGroupTests
{
    [Fact]
    public async Task PollAsync_GroupApprovals_AppearInTheFeed_AlongsideTheOtherSources()
    {
        var groups = NewGroups(GroupPending("req-g1", "Alice", "member", "Contoso SQL Admins"));
        var watcher = NewWatcher(
            NewGraph(approvals: new[] { GraphPending("approval-1", "Bob", "Owner") }),
            NewArm(),
            groups,
            NewNotifier());

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        Assert.Equal(2, watcher.CurrentApprovals.Count);
        var group = Assert.Single(watcher.CurrentApprovals, a => a.Source == PimSource.EntraGroup);

        // The approval's id IS its schedule request's id — nothing has to be
        // parsed out of a resource path the way ARM's is.
        Assert.Equal("req-g1", group.ApprovalId);
        Assert.Equal("Alice", group.PrincipalDisplay);
        // Access id in the role slot, group in the scope slot.
        Assert.Equal("Member", group.RoleDisplay);
        Assert.Equal("Contoso SQL Admins", group.ScopeDisplay);
        Assert.Null(group.ArmScope);
    }

    [Fact]
    public async Task PollAsync_GroupFeedFails_LeavesTheGraphAndArmApprovals()
    {
        var groups = Substitute.For<IGraphGroupPimClient>();
        groups.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("403"));

        var watcher = NewWatcher(
            NewGraph(approvals: new[] { GraphPending("approval-1", "Bob", "Owner") }),
            NewArm(),
            groups,
            NewNotifier());

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        var only = Assert.Single(watcher.CurrentApprovals);
        Assert.Equal(PimSource.EntraId, only.Source);
    }

    [Fact]
    public async Task PollAsync_GraphFeedFails_LeavesTheGroupApprovals()
    {
        var graph = NewGraph();
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("403"));

        var watcher = NewWatcher(
            graph,
            NewArm(),
            NewGroups(GroupPending("req-g1", "Alice", "owner", "Contoso SQL Admins")),
            NewNotifier());

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        var only = Assert.Single(watcher.CurrentApprovals);
        Assert.Equal(PimSource.EntraGroup, only.Source);
        Assert.Equal("Owner", only.RoleDisplay);
    }

    [Fact]
    public async Task PollAsync_GroupApproveChoice_RoutesToTheGroupReview()
    {
        var graph = NewGraph();
        var arm = NewArm();
        var groups = NewGroups(GroupPending("req-g1", "Alice", "member", "Contoso SQL Admins"));
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult("on call rotation"));

        var watcher = NewWatcher(graph, arm, groups, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await groups.Received(1).ReviewAsync(
            "req-g1", ApprovalDecision.Approve, "on call rotation", Arg.Any<CancellationToken>());
        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await arm.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // Beaten to it by another approver: an Info notification explaining what
    // happened, not an error — and nothing rethrown out of the poll.
    [Fact]
    public async Task PollAsync_GroupApprovalAlreadyDecided_TellsTheUser_WithoutAnError()
    {
        var groups = NewGroups(GroupPending("req-g1", "Alice", "member", "Contoso SQL Admins"));
        groups.ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new ApprovalAlreadyDecidedException("req-g1"));

        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult("approved"));

        var watcher = NewWatcher(NewGraph(), NewArm(), groups, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r =>
                r is InformationRequest
                && r.Severity == NotificationSeverity.Info
                && r.Title.Contains("Already decided", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    // ---- helpers ----------------------------------------------------------

    private static GroupScheduleRequest GroupPending(
        string id, string requestor, string accessId, string groupName)
        => new(
            Id: id,
            Status: "PendingApproval",
            Action: "adminAssign",
            AccessId: accessId,
            PrincipalId: "prin-9",
            GroupId: "group-1",
            Justification: "on call this week",
            ApprovalId: id,
            TargetScheduleId: null,
            CreatedDateTime: DateTimeOffset.UtcNow,
            CompletedDateTime: null,
            Principal: new EntraPrincipal("prin-9", requestor, $"{requestor}@contoso.com"),
            Group: new GroupRef("group-1", groupName));

    private static EntraScheduleRequest GraphPending(string approvalId, string requestor, string roleName)
        => new(
            Id: $"req-{approvalId}",
            Status: "PendingApproval",
            Action: "selfActivate",
            PrincipalId: "prin-8",
            RoleDefinitionId: "role-owner",
            DirectoryScopeId: "/",
            Justification: "needed",
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: approvalId,
            RequestType: null,
            Principal: new EntraPrincipal("prin-8", requestor, $"{requestor}@contoso.com"),
            RoleDefinition: new EntraRoleDefinition("role-owner", roleName, null),
            ScheduleInfo: null);

    private static IGraphGroupPimClient NewGroups(params GroupScheduleRequest[] pending)
    {
        var groups = Substitute.For<IGraphGroupPimClient>();
        groups.ListPendingApprovalsAsync(Arg.Any<CancellationToken>()).Returns(pending);
        return groups;
    }

    private static IGraphPimClient NewGraph(EntraScheduleRequest[]? approvals = null)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Returns(approvals ?? Array.Empty<EntraScheduleRequest>());
        return graph;
    }

    private static IArmPimClient NewArm()
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmSubscription>());
        arm.ListPendingApprovalsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmRoleAssignmentScheduleRequest>());
        return arm;
    }

    private static INotifier NewNotifier(
        NotificationResult? choiceResult = null,
        NotificationResult? textResult = null)
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(choiceResult ?? new DismissedResult());
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(textResult ?? new DismissedResult());
        return notifier;
    }

    private static PendingApprovalWatcher NewWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        IGraphGroupPimClient groups,
        INotifier notifier)
    {
        var context = Substitute.For<IPluginContext>();
        context.Logger.Returns(NullLogger<PendingApprovalWatcher>.Instance);
        context.Notifier.Returns(notifier);
        context.Tenants.Returns(new List<PluginTenant> { new("tenant-1", "Contoso") });

        return new PendingApprovalWatcher(
            graph,
            arm,
            groups,
            context,
            new PluginTenant("tenant-1", "Contoso"),
            TimeSpan.FromMilliseconds(50));
    }

    // PollAsync fires HandleNewApprovalAsync with `_ = ...` so completion is
    // out-of-band. Give those tasks a short slice to run before assertions.
    private static Task Settle() => Task.Delay(150);
}
