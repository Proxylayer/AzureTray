using System;
using System.Collections.Generic;
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
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// The approver's popup must show WHY the requestor is asking. Two things have
// to hold: the requestor's justification survives the fetch into
// UnifiedPendingApproval, and HandleNewApprovalAsync renders it in the visible
// Message — with the Details expander appearing only when the visible copy had
// to be clamped.
public sealed class PendingApprovalReasonTests
{
    private const string RequestorReason = "Incident #42 — need Owner to restart the gateway";

    // The approver's decision comment. Never the thing the popup shows: if this
    // string ever reaches the Message, the requestor's reason has been confused
    // with the approval step's comment.
    private const string ApproverComment = "Looks fine to me, approving";

    // ---- projection: the field survives the fetch --------------------------

    [Fact]
    public async Task PollAsync_GraphApproval_ProjectsTheRequestorsJustification()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner", RequestorReason) });
        var watcher = NewWatcher(graph, NewArm(), NewNotifier());

        await watcher.PollAsync(CancellationToken.None);

        var approval = Assert.Single(watcher.CurrentApprovals);
        Assert.Equal(RequestorReason, approval.RequestorJustification);
    }

    [Fact]
    public async Task PollAsync_GraphApproval_WithNoJustification_LeavesItNull()
    {
        var graph = NewGraph(approvals: new[] { GraphPending("approval-1", "Alice", "Owner", justification: null) });
        var watcher = NewWatcher(graph, NewArm(), NewNotifier());

        await watcher.PollAsync(CancellationToken.None);

        Assert.Null(Assert.Single(watcher.CurrentApprovals).RequestorJustification);
    }

    [Fact]
    public async Task PollAsync_ArmApproval_ProjectsTheRequestorsJustification()
    {
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[] { ArmPending("approval-arm-1", "Bob", "Contributor", "/subscriptions/sub-1", RequestorReason) });
        var watcher = NewWatcher(NewGraph(), arm, NewNotifier());

        await watcher.PollAsync(CancellationToken.None);

        var approval = Assert.Single(watcher.CurrentApprovals);
        Assert.Equal(RequestorReason, approval.RequestorJustification);
    }

    [Fact]
    public async Task PollAsync_ArmApproval_WithNoJustification_LeavesItNull()
    {
        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[] { ArmPending("approval-arm-1", "Bob", "Contributor", "/subscriptions/sub-1", justification: null) });
        var watcher = NewWatcher(NewGraph(), arm, NewNotifier());

        await watcher.PollAsync(CancellationToken.None);

        Assert.Null(Assert.Single(watcher.CurrentApprovals).RequestorJustification);
    }

    // The schedule request carries the requestor's reason; the approval step /
    // stage carries the approver's decision comment. Pin that the popup is fed
    // from the former even when both are present and different — mixing them up
    // is the single most likely way this feature breaks.
    [Fact]
    public async Task PollAsync_GraphApproval_TakesTheRequestsJustification_NotTheApprovalSteps()
    {
        var request = GraphPending("approval-1", "Alice", "Owner", RequestorReason);
        // The approver's comment, as it lives on the approval step the review
        // PATCH writes back. It shares nothing with the request's own field.
        var step = new EntraApprovalStep("step-1", "InProgress", "reviewer-1", null, ApproverComment);
        Assert.NotEqual(request.Justification, step.Justification);

        var watcher = NewWatcher(NewGraph(approvals: new[] { request }), NewArm(), NewNotifier());

        await watcher.PollAsync(CancellationToken.None);

        var approval = Assert.Single(watcher.CurrentApprovals);
        Assert.Equal(RequestorReason, approval.RequestorJustification);
        Assert.NotEqual(step.Justification, approval.RequestorJustification);
    }

    [Fact]
    public async Task PollAsync_ArmApproval_TakesTheRequestsJustification_NotTheStages()
    {
        var request = ArmPending("approval-arm-1", "Bob", "Contributor", "/subscriptions/sub-1", RequestorReason);
        var stage = new ArmApprovalStageProperties("InProgress", null, ApproverComment, true);
        Assert.NotEqual(request.Properties!.Justification, stage.Justification);

        var arm = NewArm(
            subscriptions: new[] { ArmSub("sub-1", "Dev") },
            approvals: new[] { request });
        var watcher = NewWatcher(NewGraph(), arm, NewNotifier());

        await watcher.PollAsync(CancellationToken.None);

        var approval = Assert.Single(watcher.CurrentApprovals);
        Assert.Equal(RequestorReason, approval.RequestorJustification);
        Assert.NotEqual(stage.Justification, approval.RequestorJustification);
    }

    // ---- popup content -----------------------------------------------------

    [Fact]
    public async Task HandleNewApprovalAsync_MessageCarriesPrincipalRoleScopeAndReason()
    {
        var notifier = NewNotifier();
        var watcher = NewWatcher(NewGraph(), NewArm(), notifier);

        await watcher.HandleNewApprovalAsync(Unified(RequestorReason), CancellationToken.None);

        var choice = SingleChoice(notifier);
        Assert.Equal(
            $"Alice is requesting Owner on Entra ID directory.\n\nReason: \"{RequestorReason}\"",
            choice.Message);
        // The blank line between the ask and the reason is what keeps the two
        // readable as separate paragraphs in the popup.
        Assert.Contains("directory.\n\nReason:", choice.Message, StringComparison.Ordinal);
        Assert.Equal("PIM approval — Contoso", choice.Title);
        Assert.Equal(new[] { "Approve", "Reject" }, choice.Choices);
    }

    // A reason that fits needs no expander — the popup must look exactly as it
    // did before this feature for the common case.
    [Fact]
    public async Task HandleNewApprovalAsync_WithReasonThatFits_AttachesNoDetails()
    {
        var notifier = NewNotifier();
        var watcher = NewWatcher(NewGraph(), NewArm(), notifier);

        await watcher.HandleNewApprovalAsync(Unified(RequestorReason), CancellationToken.None);

        Assert.Null(SingleChoice(notifier).Details);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \r\n  ")]
    public async Task HandleNewApprovalAsync_WithNoReason_SaysSo_AndAttachesNoDetails(string? justification)
    {
        var notifier = NewNotifier();
        var watcher = NewWatcher(NewGraph(), NewArm(), notifier);

        await watcher.HandleNewApprovalAsync(Unified(justification), CancellationToken.None);

        var choice = SingleChoice(notifier);
        Assert.Equal(
            "Alice is requesting Owner on Entra ID directory.\n\nNo reason was given for this request.",
            choice.Message);
        Assert.Null(choice.Details);
    }

    [Fact]
    public async Task HandleNewApprovalAsync_WithOverLongReason_ClampsMessage_AndPutsTheFullTextInDetails()
    {
        var full = new string('r', ApprovalReason.MaxMessageLength + 25);
        var notifier = NewNotifier();
        var watcher = NewWatcher(NewGraph(), NewArm(), notifier);

        await watcher.HandleNewApprovalAsync(Unified(full), CancellationToken.None);

        var choice = SingleChoice(notifier);
        Assert.Contains(
            $"Reason: \"{new string('r', ApprovalReason.MaxMessageLength)}…\"",
            choice.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(full, choice.Message, StringComparison.Ordinal);

        var detail = Assert.Single(choice.Details!);
        Assert.Equal("Reason", detail.Name);
        Assert.Equal(full, detail.Value);
    }

    // ---- the decision path is undisturbed ----------------------------------

    // The approver still types their own justification and that — not the
    // requestor's reason — is what reaches the API.
    [Fact]
    public async Task HandleNewApprovalAsync_SubmitsTheApproversOwnJustification_NotTheRequestors()
    {
        var graph = NewGraph();
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new TextInputResult(ApproverComment));
        var watcher = NewWatcher(graph, NewArm(), notifier);

        await watcher.HandleNewApprovalAsync(Unified(RequestorReason), CancellationToken.None);

        await graph.Received(1).ReviewAsync(
            "approval-1", ApprovalDecision.Approve, ApproverComment, Arg.Any<CancellationToken>());
        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(), RequestorReason, Arg.Any<CancellationToken>());

        // Second prompt, unchanged: the approver's mandatory justification.
        var prompt = Assert.IsType<TextInputRequest>(Shown(notifier)[1]);
        Assert.Equal("Justification — Approve", prompt.Title);
        Assert.Equal("Required", prompt.Placeholder);
    }

    [Fact]
    public async Task HandleNewApprovalAsync_WhenJustificationPromptDismissed_MakesNoApiCall()
    {
        var graph = NewGraph();
        var notifier = NewNotifier(
            choiceResult: new ChoiceResult("Approve", null),
            textResult: new DismissedResult());
        var watcher = NewWatcher(graph, NewArm(), notifier);

        await watcher.HandleNewApprovalAsync(Unified(RequestorReason), CancellationToken.None);

        await graph.DidNotReceive().ReviewAsync(
            Arg.Any<string>(), Arg.Any<ApprovalDecision>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- builders ----------------------------------------------------------

    private static UnifiedPendingApproval Unified(string? requestorJustification)
        => new(
            Source: PimSource.EntraId,
            ApprovalId: "approval-1",
            PrincipalDisplay: "Alice",
            RoleDisplay: "Owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            RequestorPrincipalId: "other-objectid",
            RequestorJustification: requestorJustification);

    private static EntraScheduleRequest GraphPending(
        string approvalId, string principalDisplayName, string roleDisplayName, string? justification)
        => new(
            Id: $"req-{approvalId}",
            Status: "PendingApproval",
            Action: "selfActivate",
            PrincipalId: "other-objectid",
            RoleDefinitionId: null,
            DirectoryScopeId: "/",
            Justification: justification,
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: approvalId,
            RequestType: null,
            Principal: new EntraPrincipal("other-objectid", principalDisplayName, null),
            RoleDefinition: new EntraRoleDefinition(null, roleDisplayName, null),
            ScheduleInfo: null);

    private static ArmSubscription ArmSub(string id, string displayName)
        => new($"/subscriptions/{id}", id, displayName, "Enabled");

    private static ArmRoleAssignmentScheduleRequest ArmPending(
        string approvalId, string principalDisplayName, string roleDisplayName, string scope, string? justification)
        => new(
            Id: $"/.../req-{approvalId}",
            Name: $"req-{approvalId}",
            Type: null,
            Properties: new ArmRoleRequestProperties(
                Status: "PendingApproval",
                PrincipalId: "other-objectid",
                RoleDefinitionId: null,
                Scope: scope,
                Justification: justification,
                RequestType: "AdminAdd",
                ApprovalId: approvalId,
                CreatedOn: DateTimeOffset.UtcNow,
                ExpandedProperties: new ArmExpandedProperties(
                    Principal: new ArmPrincipalDto("other-objectid", principalDisplayName, "User", null),
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

    // INotifier has a single method, so every recorded call is a ShowAsync and
    // its first argument is the request that was displayed, in order.
    private static List<NotificationRequest> Shown(INotifier notifier)
        => notifier.ReceivedCalls()
            .Select(c => (NotificationRequest)c.GetArguments()[0]!)
            .ToList();

    private static ChoiceRequest SingleChoice(INotifier notifier)
        => Assert.Single(Shown(notifier).OfType<ChoiceRequest>());
}
