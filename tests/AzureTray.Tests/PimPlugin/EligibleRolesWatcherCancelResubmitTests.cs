using System;
using System.Collections.Generic;
using System.IO;
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
using AzureTray.Plugin.PIM.Groups.Dto;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Ctrl+click (or the context item) on a "(pending)" row: HandleCancelAndResubmitAsync
// re-finds the tracked request, asks for confirmation, withdraws the request via
// the source's own client, and only then re-runs the normal activation flow.
// Order matters everywhere here:
//
//   * declined confirm → nothing is cancelled and nothing is submitted;
//   * a failing cancel leaves the request TRACKED (the status poll reconciles)
//     and submits nothing, or a request the approver was deciding gets doubled;
//   * only a successful cancel untracks and resubmits — and a resubmission that
//     itself goes to approval tracks the NEW request id.
public sealed class EligibleRolesWatcherCancelResubmitTests : IDisposable
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

    // ---- accepted confirm ---------------------------------------------------

    [Fact]
    public async Task Accepted_Entra_CancelsTheTrackedRequest_ThenSubmitsAFreshActivation()
    {
        var h = NewHarness();
        h.Store.Track(EntraPending("req-1"));
        ConfirmWith(h, accepted: true);
        h.Graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-2", "PendingApproval"));

        await h.Watcher.HandleCancelAndResubmitAsync(EntraRole(), CancellationToken.None);

        await h.Graph.Received(1).CancelActivationAsync("req-1", Arg.Any<CancellationToken>());
        await h.Graph.Received(1).ActivateRoleAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The old request is gone and the NEW one — itself awaiting approval —
        // is what the store tracks now.
        var tracked = Assert.Single(h.Store.Current);
        Assert.Equal("req-2", tracked.RequestId);
    }

    [Fact]
    public async Task ConfirmPrompt_NamesTheRole()
    {
        var h = NewHarness();
        h.Store.Track(EntraPending("req-1"));
        ConfirmWith(h, accepted: true);
        h.Graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-2", "Provisioned"));

        await h.Watcher.HandleCancelAndResubmitAsync(EntraRole(), CancellationToken.None);

        await h.Notifier.Received(1).ShowAsync(
            Arg.Is<YesNoRequest>(r => r.Title == "Cancel request for Owner?"),
            Arg.Any<CancellationToken>());
    }

    // ---- declined confirm ---------------------------------------------------

    [Fact]
    public async Task Declined_CancelsNothing_SubmitsNothing_AndTheRequestStaysTracked()
    {
        var h = NewHarness();
        h.Store.Track(EntraPending("req-1"));
        ConfirmWith(h, accepted: false);

        await h.Watcher.HandleCancelAndResubmitAsync(EntraRole(), CancellationToken.None);

        await h.Graph.DidNotReceiveWithAnyArgs().CancelActivationAsync(default!, default);
        await h.Graph.DidNotReceiveWithAnyArgs().ActivateRoleAsync(
            default!, default!, default!, default, default!, default);
        Assert.Equal("req-1", Assert.Single(h.Store.Current).RequestId);
    }

    // ---- failing cancel -----------------------------------------------------

    [Fact]
    public async Task CancelFailure_KeepsTheRequestTracked_NotifiesTheError_AndDoesNotResubmit()
    {
        var h = NewHarness();
        h.Store.Track(EntraPending("req-1"));
        ConfirmWith(h, accepted: true);
        h.Graph.CancelActivationAsync("req-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("Graph POST cancel returned 400 BadRequest")));

        await h.Watcher.HandleCancelAndResubmitAsync(EntraRole(), CancellationToken.None);

        // Still tracked: the status poll reconciles a request the approver
        // decided in the meantime; an actually-pending one stays greyed.
        Assert.Equal("req-1", Assert.Single(h.Store.Current).RequestId);
        await h.Graph.DidNotReceiveWithAnyArgs().ActivateRoleAsync(
            default!, default!, default!, default, default!, default);
        await h.Notifier.Received(1).ShowAsync(
            Arg.Is<InformationRequest>(r =>
                r.Title == "Cancellation failed: Owner"
                && r.Severity == NotificationSeverity.Error),
            Arg.Any<CancellationToken>());
    }

    // ---- stale row ----------------------------------------------------------

    [Fact]
    public async Task NoPendingRequestForTheRole_PromptsNothing_CallsNothing()
    {
        var h = NewHarness();

        await h.Watcher.HandleCancelAndResubmitAsync(EntraRole(), CancellationToken.None);

        await h.Notifier.DidNotReceive().ShowAsync(Arg.Any<YesNoRequest>(), Arg.Any<CancellationToken>());
        await h.Graph.DidNotReceiveWithAnyArgs().CancelActivationAsync(default!, default);
        await h.Graph.DidNotReceiveWithAnyArgs().ActivateRoleAsync(
            default!, default!, default!, default, default!, default);
    }

    // ---- per-source routing -------------------------------------------------

    [Fact]
    public async Task Arm_RoutesTheCancelToTheArmClient_WithTheRequestsScope()
    {
        var h = NewHarness();
        h.Store.Track(ArmPending("req-arm-1"));
        ConfirmWith(h, accepted: true);
        h.Arm.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ArmRequest("req-arm-2", "Provisioned"));

        await h.Watcher.HandleCancelAndResubmitAsync(ArmRole(), CancellationToken.None);

        await h.Arm.Received(1).CancelActivationAsync(
            "/subscriptions/sub-1", "req-arm-1", Arg.Any<CancellationToken>());
        await h.Graph.DidNotReceiveWithAnyArgs().CancelActivationAsync(default!, default);
        await h.Groups.DidNotReceiveWithAnyArgs().CancelActivationAsync(default!, default);

        // The resubmission provisioned outright, so nothing is tracked anymore.
        Assert.Empty(h.Store.Current);
    }

    [Fact]
    public async Task Group_RoutesTheCancelToTheGroupClient()
    {
        var h = NewHarness();
        h.Store.Track(GroupPending("req-grp-1"));
        ConfirmWith(h, accepted: true);
        h.Groups.ActivateAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GroupRequest("req-grp-2", "Provisioned"));

        await h.Watcher.HandleCancelAndResubmitAsync(GroupRole(), CancellationToken.None);

        await h.Groups.Received(1).CancelActivationAsync("req-grp-1", Arg.Any<CancellationToken>());
        await h.Graph.DidNotReceiveWithAnyArgs().CancelActivationAsync(default!, default);
        await h.Arm.DidNotReceiveWithAnyArgs().CancelActivationAsync(default!, default!, default);
        Assert.Empty(h.Store.Current);
    }

    // ---- builders ---------------------------------------------------------

    private static UnifiedEligibleRole EntraRole()
        => new(
            Source: PimSource.EntraId,
            RoleName: "Owner",
            RoleDefinitionId: "role-owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            EligibilityId: "elig-1");

    private static UnifiedEligibleRole ArmRole()
        => new(
            Source: PimSource.AzureRbac,
            RoleName: "Contributor",
            RoleDefinitionId: "arm-role-contrib",
            ScopeDisplay: "Dev (sub)",
            ArmScope: "/subscriptions/sub-1",
            EligibilityId: "elig-arm-1");

    private static UnifiedEligibleRole GroupRole()
        => new(
            Source: PimSource.EntraGroup,
            RoleName: "Member",
            RoleDefinitionId: "member",
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            EligibilityId: "elig-grp-1",
            GroupId: "grp-1");

    private static PendingActivationRequest EntraPending(string requestId)
        => new(
            Source: PimSource.EntraId,
            RequestId: requestId,
            RoleName: "Owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            SubmittedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            RoleDefinitionId: "role-owner",
            DirectoryScopeId: "/");

    private static PendingActivationRequest ArmPending(string requestId)
        => new(
            Source: PimSource.AzureRbac,
            RequestId: requestId,
            RoleName: "Contributor",
            ScopeDisplay: "Dev (sub)",
            ArmScope: "/subscriptions/sub-1",
            SubmittedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            RoleDefinitionId: "arm-role-contrib");

    private static PendingActivationRequest GroupPending(string requestId)
        => new(
            Source: PimSource.EntraGroup,
            RequestId: requestId,
            RoleName: "Member",
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            SubmittedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            RoleDefinitionId: "member",
            GroupId: "grp-1");

    private static EntraScheduleRequest GraphRequest(string? id, string status)
        => new(
            Id: id,
            Status: status,
            Action: "selfActivate",
            PrincipalId: "prin-1",
            RoleDefinitionId: "role-owner",
            DirectoryScopeId: "/",
            Justification: "operations",
            CreatedDateTime: DateTimeOffset.UtcNow,
            ApprovalId: null,
            RequestType: null,
            Principal: null,
            RoleDefinition: null,
            ScheduleInfo: null);

    private static ArmRoleAssignmentScheduleRequest ArmRequest(string? name, string status)
        => new(
            Id: "/subscriptions/sub-1/providers/Microsoft.Authorization/roleAssignmentScheduleRequests/req-from-id",
            Name: name,
            Type: null,
            Properties: new ArmRoleRequestProperties(
                Status: status,
                PrincipalId: "prin-1",
                RoleDefinitionId: "arm-role-contrib",
                Scope: "/subscriptions/sub-1",
                Justification: "incident #42",
                RequestType: "SelfActivate",
                ApprovalId: null,
                CreatedOn: DateTimeOffset.UtcNow,
                ExpandedProperties: null,
                ScheduleInfo: null,
                LinkedRoleEligibilityScheduleId: null));

    private static GroupScheduleRequest GroupRequest(string? id, string status)
        => new(
            Id: id,
            Status: status,
            Action: "selfActivate",
            AccessId: "member",
            PrincipalId: "prin-1",
            GroupId: "grp-1",
            Justification: "operations",
            ApprovalId: null,
            TargetScheduleId: null,
            CreatedDateTime: DateTimeOffset.UtcNow,
            CompletedDateTime: null,
            Principal: null,
            Group: null);

    // ---- harness ----------------------------------------------------------

    private sealed record Harness(
        EligibleRolesWatcher Watcher,
        PendingActivationStore Store,
        INotifier Notifier,
        IGraphPimClient Graph,
        IArmPimClient Arm,
        IGraphGroupPimClient Groups);

    private static void ConfirmWith(Harness h, bool accepted)
        => h.Notifier.ShowAsync(Arg.Any<YesNoRequest>(), Arg.Any<CancellationToken>())
            .Returns(new YesNoResult(accepted));

    private Harness NewHarness()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());

        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmSubscription>());

        var groups = Substitute.For<IGraphGroupPimClient>();

        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherCancelResubmitTests>.Instance);
        ctx.DataDir.Returns(_dataDir);

        var notifier = Substitute.For<INotifier>();
        // The resubmission's duration prompt, then its justification prompt.
        notifier.ShowAsync(Arg.Any<DurationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DurationResult(TimeSpan.FromHours(4)));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("operations"));
        ctx.Notifier.Returns(notifier);

        var store = new PendingActivationStore(ctx, Tenant);
        var watcher = new EligibleRolesWatcher(
            graph, arm, groups, ctx, Tenant, TimeSpan.FromMilliseconds(50), store);
        return new Harness(watcher, store, notifier, graph, arm, groups);
    }
}
