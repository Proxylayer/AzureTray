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
            "approval-1", ApprovalDecision.Approve,
            "Needed for incident #42", Arg.Any<CancellationToken>());
        await arm.DidNotReceive().ReviewAsync(
            Arg.Any<string>(),
            Arg.Any<ApprovalDecision>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ArmApproveChoice_RoutesToArmReview()
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
            "approval-arm-1",
            ApprovalDecision.Approve,
            "operations",
            Arg.Any<CancellationToken>());
        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
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
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
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
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_DropsSeenId_WhenApprovalNoLongerListed()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
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
    public async Task PollAsync_DropsApproval_WhenRequestorIsSignedInUser()
    {
        // Two graph approvals — one authored by the signed-in user, one by
        // somebody else. Only the someone-else approval should reach the
        // notifier and the snapshot.
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("me-objectid");
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                GraphPendingFor("approval-self", "me-objectid", "Self", "Owner"),
                GraphPendingFor("approval-other", "other-objectid", "Alice", "Reader"),
            });
        var arm = NewArm();
        var notifier = NewNotifier();
        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
        Assert.DoesNotContain(watcher.CurrentApprovals, a => a.ApprovalId == "approval-self");
        Assert.Contains(watcher.CurrentApprovals, a => a.ApprovalId == "approval-other");
    }

    [Fact]
    public async Task PollAsync_WhenSignedInUserUnknown_DoesNotFilter()
    {
        // Graph /me fails or returns null — fall back to legacy behaviour:
        // surface every approval, including ones the user authored.
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { GraphPendingFor("approval-self", "me-objectid", "Self", "Owner") });
        var arm = NewArm();
        var notifier = NewNotifier();
        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_WhenArmFails_StillProcessesGraph()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner") });
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<ArmSubscription>>>(_ => throw new InvalidOperationException("ARM down"));
        var notifier = NewNotifier();

        var watcher = NewWatcher(graph, arm, notifier);

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
    }

    private const string MgScope = "/providers/Microsoft.Management/managementGroups/mg-1";

    [Fact]
    public async Task PollAsync_MgEligibilityWithNoSubscriptions_QueriesMgScope_AndNotifies()
    {
        // An MG-only user (zero subscriptions) must still fan out to the MG
        // scope — the old `subs.Count == 0` early-return would have skipped ARM
        // entirely and the approval would never surface.
        var graph = NewGraph();
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmSubscription>());
        List<string>? capturedScopes = null;
        arm.ListPendingApprovalsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedScopes = new List<string>(ci.Arg<IEnumerable<string>>());
                return (IReadOnlyList<ArmRoleAssignmentScheduleRequest>)new[]
                {
                    ArmPending("approval-mg-1", "Bob", "Contributor", MgScope),
                };
            });
        var notifier = NewNotifier();

        var watcher = NewWatcher(
            graph, arm, notifier,
            relevantSubscriptions: () => ScopeSet(),
            relevantManagementGroupScopes: () => ScopeSet(MgScope));

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        Assert.NotNull(capturedScopes);
        Assert.Contains(MgScope, capturedScopes!);
        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_CrossScopeDuplicate_NotifiesOnce_AndSnapshotHasOneEntry()
    {
        // An MG-scoped request comes back from both the MG query and every
        // descendant-subscription query. Same ApprovalId → same DedupKey →
        // must collapse before both the snapshot (menu) and the notify loop.
        var graph = NewGraph();
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[]
            {
                ArmPending("approval-dup", "Bob", "Contributor", MgScope),
                ArmPending("approval-dup", "Bob", "Contributor", MgScope),
            });
        var notifier = NewNotifier();

        var watcher = NewWatcher(
            graph, arm, notifier,
            relevantSubscriptions: () => ScopeSet("sub-1"),
            relevantManagementGroupScopes: () => ScopeSet(MgScope));

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
        Assert.Single(watcher.CurrentApprovals);
        Assert.Equal("approval-dup", watcher.CurrentApprovals[0].ApprovalId);
    }

    [Fact]
    public async Task PollAsync_NoMgEligibility_QueriesOnlySubscriptionScopes()
    {
        // Guards the "no MG eligibility → identical to today" promise: an
        // empty MG set must add nothing to the ARM fan-out.
        var graph = NewGraph();
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { ArmSub("sub-1", "Dev") });
        List<string>? capturedScopes = null;
        arm.ListPendingApprovalsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedScopes = new List<string>(ci.Arg<IEnumerable<string>>());
                return (IReadOnlyList<ArmRoleAssignmentScheduleRequest>)Array.Empty<ArmRoleAssignmentScheduleRequest>();
            });
        var notifier = NewNotifier();

        var watcher = NewWatcher(
            graph, arm, notifier,
            relevantSubscriptions: () => ScopeSet("sub-1"),
            relevantManagementGroupScopes: () => ScopeSet());

        await watcher.PollAsync(CancellationToken.None);

        Assert.NotNull(capturedScopes);
        Assert.Equal(new[] { "/subscriptions/sub-1" }, capturedScopes!);
        Assert.DoesNotContain(capturedScopes!, s => s.Contains("managementGroups", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PollAsync_MgScopedApproveChoice_RoutesToArmReview()
    {
        // ReviewAsync no longer takes a scope — roleAssignmentApprovals is
        // tenant-level. An MG-scoped approval must therefore route straight to
        // the ARM review with only the approval id; the MG scope never reaches
        // the client (the old scope-prefixed URL was a genuine 404 at MG scope).
        var graph = NewGraph();
        var arm = NewArm(
            approvals: new[] { ArmPending("approval-mg-1", "Bob", "Contributor", MgScope) });
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult("operations"));

        var watcher = NewWatcher(
            graph, arm, notifier,
            relevantSubscriptions: () => ScopeSet(),
            relevantManagementGroupScopes: () => ScopeSet(MgScope));

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await arm.Received(1).ReviewAsync(
            "approval-mg-1",
            ApprovalDecision.Approve,
            "operations",
            Arg.Any<CancellationToken>());
        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_MgScopedApprovalAuthoredBySignedInUser_IsFilteredOut()
    {
        // The self-authored filter must apply to MG-scoped results the same as
        // subscription-scoped ones.
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("me-objectid");
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraScheduleRequest>());
        var arm = NewArm(
            approvals: new[]
            {
                ArmPendingFor("approval-mg-self", "me-objectid", "Self", "Contributor", MgScope),
            });
        var notifier = NewNotifier();

        var watcher = NewWatcher(
            graph, arm, notifier,
            relevantSubscriptions: () => ScopeSet(),
            relevantManagementGroupScopes: () => ScopeSet(MgScope));

        await watcher.PollAsync(CancellationToken.None);
        await Settle();

        await notifier.DidNotReceive().ShowAsync(
            Arg.Is<NotificationRequest>(r => r is ChoiceRequest),
            Arg.Any<CancellationToken>());
        Assert.Empty(watcher.CurrentApprovals);
    }

    // ---- builders ---------------------------------------------------------

    private static EntraScheduleRequest GraphPending(string approvalId, string principalDisplayName, string roleDisplayName)
        => GraphPendingFor(approvalId, principalId: null, principalDisplayName, roleDisplayName);

    private static EntraScheduleRequest GraphPendingFor(
        string approvalId, string? principalId, string principalDisplayName, string roleDisplayName)
        => new(
            Id: $"req-{approvalId}",
            Status: "PendingApproval",
            Action: "selfActivate",
            PrincipalId: principalId,
            RoleDefinitionId: null,
            DirectoryScopeId: "/",
            Justification: null,
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: approvalId,
            RequestType: null,
            Principal: new EntraPrincipal(principalId, principalDisplayName, null),
            RoleDefinition: new EntraRoleDefinition(null, roleDisplayName, null),
            ScheduleInfo: null);

    private static ArmSubscription ArmSub(string id, string displayName)
        => new($"/subscriptions/{id}", id, displayName, "Enabled");

    private static ArmRoleAssignmentScheduleRequest ArmPending(
        string approvalId, string principalDisplayName, string roleDisplayName, string scope)
        => ArmPendingFor(approvalId, principalId: null, principalDisplayName, roleDisplayName, scope);

    private static ArmRoleAssignmentScheduleRequest ArmPendingFor(
        string approvalId, string? principalId, string principalDisplayName, string roleDisplayName, string scope)
        => new(
            Id: $"/.../req-{approvalId}",
            Name: $"req-{approvalId}",
            Type: null,
            Properties: new ArmRoleRequestProperties(
                Status: "PendingApproval",
                PrincipalId: principalId,
                RoleDefinitionId: null,
                Scope: scope,
                Justification: null,
                RequestType: "AdminAdd",
                ApprovalId: approvalId,
                CreatedOn: DateTimeOffset.UtcNow,
                ExpandedProperties: new ArmExpandedProperties(
                    Principal: new ArmPrincipalDto(principalId, principalDisplayName, "User", null),
                    RoleDefinition: new ArmRoleDefinitionDto(null, roleDisplayName, null),
                    Scope: new ArmScopeDto(null, "Dev (sub)", "subscription")),
                ScheduleInfo: null,
                LinkedRoleEligibilityScheduleId: null));

    private static IGraphPimClient NewGraph(IReadOnlyList<EntraScheduleRequest>? approvals = null)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.ListPendingApprovalsAsync(Arg.Any<CancellationToken>())
            .Returns(approvals ?? Array.Empty<EntraScheduleRequest>());
        return graph;
    }

    private static IArmPimClient NewArm(
        IReadOnlyList<ArmSubscription>? subscriptions = null,
        IReadOnlyList<ArmRoleAssignmentScheduleRequest>? approvals = null)
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(subscriptions ?? Array.Empty<ArmSubscription>());
        arm.ListPendingApprovalsAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
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

    private static PendingApprovalWatcher NewWatcher(
        IGraphPimClient graph,
        IArmPimClient arm,
        INotifier notifier,
        Func<IReadOnlySet<string>>? relevantSubscriptions = null,
        Func<IReadOnlySet<string>>? relevantManagementGroupScopes = null)
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
            TimeSpan.FromMilliseconds(50),
            relevantSubscriptions,
            relevantManagementGroupScopes);
    }

    private static HashSet<string> ScopeSet(params string[] scopes)
        => new(scopes, StringComparer.OrdinalIgnoreCase);

    // PollAsync fires HandleNewApprovalAsync with `_ = ...` so completion is
    // out-of-band. Give those tasks a short slice to run before assertions.
    private static Task Settle() => Task.Delay(150);
}
