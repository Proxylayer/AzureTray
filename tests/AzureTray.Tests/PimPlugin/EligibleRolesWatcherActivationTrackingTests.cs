using System;
using System.Collections.Generic;
using System.IO;
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

// HandleActivationAsync now reads the activation response's id + status: an
// activation that did not come back Provisioned went to an approver and is
// recorded so PendingActivationWatcher can follow it up.
public sealed class EligibleRolesWatcherActivationTrackingTests : IDisposable
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

    [Fact]
    public async Task HandleActivationAsync_Entra_Provisioned_RecordsNothing()
    {
        var graph = NewGraph();
        graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-1", "Provisioned"));

        var (watcher, store) = NewWatcher(graph, NewArm());

        await watcher.HandleActivationAsync(EntraRole(), CancellationToken.None);

        Assert.Empty(store.Current);
    }

    [Fact]
    public async Task HandleActivationAsync_Entra_PendingApproval_RecordsThePendingRequest()
    {
        var graph = NewGraph();
        graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-1", "PendingApproval"));

        var (watcher, store) = NewWatcher(graph, NewArm());

        await watcher.HandleActivationAsync(EntraRole(), CancellationToken.None);

        var tracked = Assert.Single(store.Current);
        Assert.Equal("req-1", tracked.RequestId);
        Assert.Equal(PimSource.EntraId, tracked.Source);
        Assert.Equal("Owner", tracked.RoleName);
        Assert.Equal("Entra ID directory", tracked.ScopeDisplay);
        Assert.Null(tracked.ArmScope);
    }

    [Theory]
    [InlineData("Granted")]
    [InlineData("PendingApprovalProvisioning")]
    [InlineData("PendingAdminDecision")]
    public async Task HandleActivationAsync_Entra_AnyNonProvisionedStatus_IsRecorded(string status)
    {
        var graph = NewGraph();
        graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-1", status));

        var (watcher, store) = NewWatcher(graph, NewArm());

        await watcher.HandleActivationAsync(EntraRole(), CancellationToken.None);

        Assert.Single(store.Current);
    }

    // A request that came back already dead is not worth polling.
    [Theory]
    [InlineData("Denied")]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    public async Task HandleActivationAsync_Entra_TerminalFailureStatus_IsNotRecorded(string status)
    {
        var graph = NewGraph();
        graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-1", status));

        var (watcher, store) = NewWatcher(graph, NewArm());

        await watcher.HandleActivationAsync(EntraRole(), CancellationToken.None);

        Assert.Empty(store.Current);
    }

    [Fact]
    public async Task HandleActivationAsync_Entra_PendingWithoutRequestId_IsNotRecorded()
    {
        var graph = NewGraph();
        graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest(null, "PendingApproval"));

        var (watcher, store) = NewWatcher(graph, NewArm());

        await watcher.HandleActivationAsync(EntraRole(), CancellationToken.None);

        Assert.Empty(store.Current);
    }

    [Fact]
    public async Task HandleActivationAsync_Arm_PendingApproval_RecordsRequestNameAndScope()
    {
        var arm = NewArm();
        arm.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ArmRequest("req-arm-1", "PendingApproval"));

        var (watcher, store) = NewWatcher(NewGraph(), arm);

        await watcher.HandleActivationAsync(ArmRole(), CancellationToken.None);

        var tracked = Assert.Single(store.Current);
        // ARM's request id for a status read is the PUT resource name, not the
        // full resource path.
        Assert.Equal("req-arm-1", tracked.RequestId);
        Assert.Equal(PimSource.AzureRbac, tracked.Source);
        Assert.Equal("/subscriptions/sub-1", tracked.ArmScope);
    }

    // When ARM omits "name" the last segment of the resource id is the name.
    [Fact]
    public async Task HandleActivationAsync_Arm_NoName_FallsBackToTheIdsLastSegment()
    {
        var arm = NewArm();
        arm.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ArmRequest(null, "PendingApproval"));

        var (watcher, store) = NewWatcher(NewGraph(), arm);

        await watcher.HandleActivationAsync(ArmRole(), CancellationToken.None);

        Assert.Equal("req-from-id", Assert.Single(store.Current).RequestId);
    }

    [Fact]
    public async Task HandleActivationAsync_Arm_Provisioned_RecordsNothing()
    {
        var arm = NewArm();
        arm.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ArmRequest("req-arm-1", "Provisioned"));

        var (watcher, store) = NewWatcher(NewGraph(), arm);

        await watcher.HandleActivationAsync(ArmRole(), CancellationToken.None);

        Assert.Empty(store.Current);
    }

    // The tracked entry survives a restart: it is written through the store's
    // file, not just held in memory.
    [Fact]
    public async Task HandleActivationAsync_PendingRequest_IsPersisted()
    {
        var graph = NewGraph();
        graph.ActivateRoleAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(GraphRequest("req-1", "PendingApproval"));

        var (watcher, _) = NewWatcher(graph, NewArm());

        await watcher.HandleActivationAsync(EntraRole(), CancellationToken.None);

        var reloaded = new PendingActivationStore(NewContext(), Tenant);
        Assert.Equal("req-1", Assert.Single(reloaded.Current).RequestId);
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

    private static IGraphPimClient NewGraph()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetSignedInUserIdAsync(Arg.Any<CancellationToken>()).Returns("prin-1");
        graph.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        graph.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EntraEligibilitySchedule>());
        return graph;
    }

    private static IArmPimClient NewArm()
    {
        var arm = Substitute.For<IArmPimClient>();
        arm.ListSubscriptionsAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmSubscription>());
        arm.ListEligibleRolesAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmEligibilitySchedule>());
        arm.ListActiveRoleAssignmentsAsync(Arg.Any<string>(), Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ArmRoleAssignmentScheduleInstance>());
        return arm;
    }

    private IPluginContext NewContext()
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<EligibleRolesWatcherActivationTrackingTests>.Instance);
        ctx.DataDir.Returns(_dataDir);

        var notifier = Substitute.For<INotifier>();
        // Duration prompt, then justification prompt.
        notifier.ShowAsync(Arg.Any<ChoiceRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ChoiceResult("4 hours", null));
        notifier.ShowAsync(Arg.Any<TextInputRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TextInputResult("operations"));
        ctx.Notifier.Returns(notifier);

        return ctx;
    }

    private (EligibleRolesWatcher Watcher, PendingActivationStore Store) NewWatcher(
        IGraphPimClient graph, IArmPimClient arm)
    {
        var ctx = NewContext();
        var store = new PendingActivationStore(ctx, Tenant);
        var watcher = new EligibleRolesWatcher(
            graph, arm, Substitute.For<IGraphGroupPimClient>(), ctx, Tenant, TimeSpan.FromMilliseconds(50), store);
        return (watcher, store);
    }
}
