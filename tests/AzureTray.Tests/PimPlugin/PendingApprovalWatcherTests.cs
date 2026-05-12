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

public sealed class PendingApprovalWatcherTests
{
    [Fact]
    public async Task PollAsync_NotifiesOnNewGraphApproval_AndDedupesOnNextPoll()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner") });
        var arm = NewArm();
        var notifier = NewNotifier();
        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest && r.Title.Contains("PIM approval")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_GraphApproveChoice_RoutesToGraphReview()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner") });
        var arm = NewArm();
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult("Needed for incident #42"));

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await graph.Received(1).ReviewAsync(
            "tenant-1", "approval-1", ApprovalDecision.Approve,
            "Needed for incident #42", Arg.Any<CancellationToken>());
        await arm.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ApprovalDecision>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ArmApproveChoice_RoutesToArmReview_WithScope()
    {
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[] { ArmPending("approval-arm-1", "Bob", "Contributor", "/subscriptions/sub-1") });
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult("operations"));

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await arm.Received(1).ReviewAsync(
            "tenant-1",
            "/subscriptions/sub-1",
            "approval-arm-1",
            ApprovalDecision.Approve,
            "operations",
            Arg.Any<CancellationToken>());
        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ArmRejectChoice_RoutesToArmReviewWithDeny()
    {
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[] { ArmPending("approval-arm-1", "Bob", "Contributor", "/subscriptions/sub-1") });
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Reject", null),
            textResult: new TextInputResult("wrong scope"));

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await arm.Received(1).ReviewAsync(
            "tenant-1",
            "/subscriptions/sub-1",
            "approval-arm-1",
            ApprovalDecision.Deny,
            "wrong scope",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_BothSourcesPending_NotifiesEachIndependently()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-graph", "Alice", "Owner") });
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[] { ArmPending("approval-arm", "Bob", "Contributor", "/subscriptions/sub-1") });
        var notifier = NewNotifier();
        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(2).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_OnChoiceDismissed_DoesNotCallReview()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner") });
        var arm = NewArm();
        var notifier = NewNotifier(choiceResult: new DismissedResult());

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_OnBlankJustification_DoesNotCallReview()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner") });
        var arm = NewArm();
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult("   "));

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_DropsSeenId_WhenApprovalNoLongerListed()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.ListPendingApprovalsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(
                new[] { GraphPending("approval-1", "Alice", "Owner") },
                Array.Empty<EntraScheduleRequest>(),
                new[] { GraphPending("approval-1", "Alice", "Owner") });

        var arm = NewArm();
        var notifier = NewNotifier();
        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None); // notifies once
        await watcher.PollAsync(CancellationToken.None); // drops from seen
        await watcher.PollAsync(CancellationToken.None); // notifies again
        await Settle();

        await notifier.Received(2).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_PublishesCurrentApprovalsSnapshot()
    {
        var graph = NewGraph(approvals: new[]
        {
            GraphPending("approval-1", "Alice", "Owner"),
            GraphPending("approval-2", "Bob", "Reader"),
        });
        var arm = NewArm();
        var notifier = NewNotifier();
        var watcher = NewWatcher(graph, arm, notifier);

        Assert.Empty(watcher.CurrentApprovals);

        await watcher.PollAsync(CancellationToken.None);

        var snapshot = watcher.CurrentApprovals;
        Assert.Equal(2, snapshot.Count);
        Assert.Contains(snapshot, a => a.ApprovalId == "approval-1" && a.PrincipalDisplay == "Alice");
        Assert.Contains(snapshot, a => a.ApprovalId == "approval-2" && a.PrincipalDisplay == "Bob");
    }

    [Fact]
    public async Task PollAsync_WhenArmFails_StillProcessesGraph()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner") });
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ArmSubscription>>>(_ => throw new InvalidOperationException("ARM down"));
        var notifier = NewNotifier();

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
    }

    // ---- builders ---------------------------------------------------------

    private static EntraScheduleRequest GraphPending(string approvalId, string principalDisplayName, string roleDisplayName)
        => new(
            Id: $"req-{approvalId}",
            Status: "PendingApproval",
            Action: "selfActivate",
            PrincipalId: null,
            RoleDefinitionId: null,
            DirectoryScopeId: "/",
            Justification: null,
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: approvalId,
            RequestType: null,
            Principal: new EntraPrincipal(null, principalDisplayName, null),
            RoleDefinition: new EntraRoleDefinition(null, roleDisplayName, null),
            ScheduleInfo: null);

    private static ArmSubscription ArmSub(string id, string displayName)
        => new($"/subscriptions/{id}", id, displayName, "Enabled");

    private static ArmRoleAssignmentScheduleRequest ArmPending(
        string approvalId, string principalDisplayName, string roleDisplayName, string scope)
        => new(
            Id: $"/.../req-{approvalId}",
            Name: $"req-{approvalId}",
            Type: null,
            Properties: new ArmRoleRequestProperties(
                Status: "PendingApproval",
                PrincipalId: null,
                RoleDefinitionId: null,
                Scope: scope,
                Justification: null,
                RequestType: "AdminAdd",
                ApprovalId: approvalId,
                CreatedOn: DateTimeOffset.UtcNow,
                ExpandedProperties: new ArmExpandedProperties(
                    Principal: new ArmPrincipalDto(null, principalDisplayName, "User", null),
                    RoleDefinition: new ArmRoleDefinitionDto(null, roleDisplayName, null),
                    Scope: new ArmScopeDto(null, "Dev (sub)", "subscription")),
                ScheduleInfo: null,
                LinkedRoleEligibilityScheduleId: null));

    private static IGraphPimClient NewGraph(IReadOnlyList<EntraScheduleRequest>? approvals = null)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.ListPendingApprovalsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(approvals ?? Array.Empty<EntraScheduleRequest>());
        return graph;
    }

    private static IArmPimClient NewArm(
        IReadOnlyList<ArmSubscription>? subscriptions = null,
        IReadOnlyList<ArmRoleAssignmentScheduleRequest>? approvals = null)
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(subscriptions ?? Array.Empty<ArmSubscription>());
        arm.ListPendingApprovalsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(approvals ?? Array.Empty<ArmRoleAssignmentScheduleRequest>());
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

    private static PendingApprovalWatcher NewWatcher(IGraphPimClient graph, IArmPimClient arm, INotifier notifier)
    {
        var context = Substitute.For<IPluginContext>();
        context.Logger.Returns(NullLogger<PendingApprovalWatcher>.Instance);
        context.Notifier.Returns(notifier);
        context.Tenants.Returns(new List<PluginTenant> { new("tenant-1", "Contoso") });

        return new PendingApprovalWatcher(
            graph,
            arm,
            context,
            new PluginTenant("tenant-1", "Contoso"),
            TimeSpan.FromMilliseconds(50));
    }

    // PollAsync fires HandleNewApprovalAsync with `_ = ...` so completion is
    // out-of-band. Give those tasks a short slice to run before assertions.
    private static Task Settle() => Task.Delay(150);
}
