using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM.Arm;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

public sealed class PendingActivationWatcherTests : IDisposable
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
    public async Task PollAsync_Provisioned_RefreshesToken_RepollsRoles_RaisesEvent_AndStopsTracking()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetActivationStatusAsync("req-1", Arg.Any<CancellationToken>()).Returns("Provisioned");
        var context = NewContext();
        context.RefreshTokenAsync(Tenant.TenantId, Arg.Any<CancellationToken>()).Returns(true);

        var store = NewStore(context);
        store.Track(EntraRequest("req-1", DateTimeOffset.UtcNow));

        var refreshes = 0;
        var provisionedEvents = 0;
        var watcher = NewWatcher(graph, context, store, _ => { refreshes++; return Task.CompletedTask; });
        watcher.ActivationProvisioned += () => provisionedEvents++;

        await watcher.PollAsync(CancellationToken.None);

        await context.Received(1).RefreshTokenAsync(Tenant.TenantId, Arg.Any<CancellationToken>());
        Assert.Equal(1, refreshes);
        Assert.Equal(1, provisionedEvents);
        Assert.Equal(0, watcher.TrackedCount);
        Assert.Empty(store.Current);
        await context.Notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r.Severity == NotificationSeverity.Success),
            Arg.Any<CancellationToken>());
    }

    // An old host returns false from the contract's default RefreshTokenAsync.
    // That is not an error and must not short-circuit the rest of the sequence.
    [Fact]
    public async Task PollAsync_Provisioned_RefreshTokenFalse_StillRepollsRolesAndRaisesEvent()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetActivationStatusAsync("req-1", Arg.Any<CancellationToken>()).Returns("Provisioned");
        var context = NewContext();
        context.RefreshTokenAsync(Tenant.TenantId, Arg.Any<CancellationToken>()).Returns(false);

        var store = NewStore(context);
        store.Track(EntraRequest("req-1", DateTimeOffset.UtcNow));

        var refreshes = 0;
        var provisionedEvents = 0;
        var watcher = NewWatcher(graph, context, store, _ => { refreshes++; return Task.CompletedTask; });
        watcher.ActivationProvisioned += () => provisionedEvents++;

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(1, refreshes);
        Assert.Equal(1, provisionedEvents);
        Assert.Equal(0, watcher.TrackedCount);
    }

    // A failing role re-poll must not swallow the approval notification or the
    // menu-refresh event either.
    [Fact]
    public async Task PollAsync_Provisioned_RolePollThrowing_StillRaisesEvent()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetActivationStatusAsync("req-1", Arg.Any<CancellationToken>()).Returns("Provisioned");
        var context = NewContext();

        var store = NewStore(context);
        store.Track(EntraRequest("req-1", DateTimeOffset.UtcNow));

        var provisionedEvents = 0;
        var watcher = NewWatcher(graph, context, store,
            _ => throw new HttpRequestException("graph is down"));
        watcher.ActivationProvisioned += () => provisionedEvents++;

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(1, provisionedEvents);
        Assert.Equal(0, watcher.TrackedCount);
    }

    [Theory]
    [InlineData("Denied")]
    [InlineData("AdminDenied")]
    [InlineData("Failed")]
    [InlineData("Canceled")]
    [InlineData("TimedOut")]
    public async Task PollAsync_TerminalRefusal_DropsTheRequest_AndWarns(string status)
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetActivationStatusAsync("req-1", Arg.Any<CancellationToken>()).Returns(status);
        var context = NewContext();

        var store = NewStore(context);
        store.Track(EntraRequest("req-1", DateTimeOffset.UtcNow));

        var refreshes = 0;
        var provisionedEvents = 0;
        var watcher = NewWatcher(graph, context, store, _ => { refreshes++; return Task.CompletedTask; });
        watcher.ActivationProvisioned += () => provisionedEvents++;

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(0, watcher.TrackedCount);
        Assert.Equal(0, provisionedEvents);
        Assert.Equal(0, refreshes);
        await context.DidNotReceive().RefreshTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await context.Notifier.Received(1).ShowAsync(
            Arg.Is<NotificationRequest>(r => r.Severity == NotificationSeverity.Warning),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_StillPending_KeepsTrackingAndDoesNothingElse()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetActivationStatusAsync("req-1", Arg.Any<CancellationToken>()).Returns("PendingApproval");
        var context = NewContext();

        var store = NewStore(context);
        store.Track(EntraRequest("req-1", DateTimeOffset.UtcNow));

        var provisionedEvents = 0;
        var watcher = NewWatcher(graph, context, store);
        watcher.ActivationProvisioned += () => provisionedEvents++;

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(1, watcher.TrackedCount);
        Assert.Equal(0, provisionedEvents);
        await context.DidNotReceive().RefreshTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await context.Notifier.DidNotReceive().ShowAsync(
            Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>());
    }

    // A transient status read failure means "couldn't tell this cycle" — the
    // request must survive for the next poll.
    [Fact]
    public async Task PollAsync_StatusReadThrows_KeepsTracking()
    {
        var graph = Substitute.For<IGraphPimClient>();
        graph.GetActivationStatusAsync("req-1", Arg.Any<CancellationToken>())
            .Returns<string?>(_ => throw new HttpRequestException("503"));
        var context = NewContext();

        var store = NewStore(context);
        store.Track(EntraRequest("req-1", DateTimeOffset.UtcNow));

        var watcher = NewWatcher(graph, context, store);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(1, watcher.TrackedCount);
    }

    [Fact]
    public async Task PollAsync_RequestOlderThan24h_IsDroppedWithoutAStatusRead()
    {
        var graph = Substitute.For<IGraphPimClient>();
        var context = NewContext();

        var store = NewStore(context);
        store.Track(EntraRequest("req-stale", DateTimeOffset.UtcNow.AddHours(-25)));

        var watcher = NewWatcher(graph, context, store);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(0, watcher.TrackedCount);
        await graph.DidNotReceive().GetActivationStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_ArmRequest_ReadsStatusAtItsScope()
    {
        var graph = Substitute.For<IGraphPimClient>();
        var arm = Substitute.For<IArmPimClient>();
        arm.GetActivationStatusAsync("/subscriptions/sub-1", "req-arm", Arg.Any<CancellationToken>())
            .Returns("Provisioned");
        var context = NewContext();

        var store = NewStore(context);
        store.Track(new PendingActivationRequest(
            Source: PimSource.AzureRbac,
            RequestId: "req-arm",
            RoleName: "Contributor",
            ScopeDisplay: "Dev (sub)",
            ArmScope: "/subscriptions/sub-1",
            SubmittedAt: DateTimeOffset.UtcNow));

        var provisionedEvents = 0;
        var watcher = NewWatcher(graph, context, store, arm: arm);
        watcher.ActivationProvisioned += () => provisionedEvents++;

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(1, provisionedEvents);
        Assert.Equal(0, watcher.TrackedCount);
        await graph.DidNotReceive().GetActivationStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // No scope means the ARM status URL can't be built; the entry stays until it
    // ages out rather than being silently declared approved.
    [Fact]
    public async Task PollAsync_ArmRequestWithoutScope_KeepsTracking()
    {
        var graph = Substitute.For<IGraphPimClient>();
        var arm = Substitute.For<IArmPimClient>();
        var context = NewContext();

        var store = NewStore(context);
        store.Track(new PendingActivationRequest(
            Source: PimSource.AzureRbac,
            RequestId: "req-arm",
            RoleName: "Contributor",
            ScopeDisplay: "Dev (sub)",
            ArmScope: null,
            SubmittedAt: DateTimeOffset.UtcNow));

        var watcher = NewWatcher(graph, context, store, arm: arm);

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(1, watcher.TrackedCount);
        await arm.DidNotReceive().GetActivationStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PollAsync_NothingTracked_MakesNoCalls()
    {
        var graph = Substitute.For<IGraphPimClient>();
        var context = NewContext();
        var watcher = NewWatcher(graph, context, NewStore(context));

        await watcher.PollAsync(CancellationToken.None);

        Assert.Equal(0, watcher.TrackedCount);
        await graph.DidNotReceive().GetActivationStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await context.DidNotReceive().RefreshTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // A group activation is polled through the group client, addressed by the
    // request id alone — the URL carries no group segment, so a tracked group
    // request needs nothing the other sources do not already persist. Sending
    // it to the directory-role client instead would 404 forever and the user's
    // approved access would never trigger the token refresh.
    [Fact]
    public async Task PollAsync_GroupRequest_PollsTheGroupClient_NotTheDirectoryRoleOne()
    {
        var graph = Substitute.For<IGraphPimClient>();
        var arm = Substitute.For<IArmPimClient>();
        var groups = Substitute.For<IGraphGroupPimClient>();
        groups.GetActivationStatusAsync("req-g1", Arg.Any<CancellationToken>()).Returns("Provisioned");

        var context = NewContext();
        var store = NewStore(context);
        store.Track(GroupRequest("req-g1", DateTimeOffset.UtcNow));

        var refreshes = 0;
        var watcher = NewWatcher(
            graph, context, store, _ => { refreshes++; return Task.CompletedTask; }, arm, groups);

        await watcher.PollAsync(CancellationToken.None);

        await groups.Received(1).GetActivationStatusAsync("req-g1", Arg.Any<CancellationToken>());
        await graph.DidNotReceive().GetActivationStatusAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await arm.DidNotReceive().GetActivationStatusAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Provisioned is terminal: the token is refreshed and tracking stops.
        Assert.Equal(1, refreshes);
        Assert.Equal(0, watcher.TrackedCount);
    }

    // ---- helpers ----------------------------------------------------------

    private static PendingActivationRequest GroupRequest(string requestId, DateTimeOffset submittedAt)
        => new(
            Source: PimSource.EntraGroup,
            RequestId: requestId,
            RoleName: "Member",
            ScopeDisplay: "Contoso SQL Admins",
            ArmScope: null,
            SubmittedAt: submittedAt);

    private static PendingActivationRequest EntraRequest(string requestId, DateTimeOffset submittedAt)
        => new(
            Source: PimSource.EntraId,
            RequestId: requestId,
            RoleName: "Owner",
            ScopeDisplay: "Entra ID directory",
            ArmScope: null,
            SubmittedAt: submittedAt);

    private static PendingActivationStore NewStore(IPluginContext context)
        => new(context, Tenant);

    private static PendingActivationWatcher NewWatcher(
        IGraphPimClient graph,
        IPluginContext context,
        PendingActivationStore store,
        Func<CancellationToken, Task>? refreshActiveRoles = null,
        IArmPimClient? arm = null,
        IGraphGroupPimClient? groups = null)
        => new(
            graph,
            arm ?? Substitute.For<IArmPimClient>(),
            groups ?? Substitute.For<IGraphGroupPimClient>(),
            context,
            Tenant,
            TimeSpan.FromMilliseconds(50),
            store,
            refreshActiveRoles ?? (_ => Task.CompletedTask));

    private IPluginContext NewContext()
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PendingActivationWatcherTests>.Instance);
        ctx.Notifier.Returns(Substitute.For<INotifier>());
        ctx.DataDir.Returns(_dataDir);
        return ctx;
    }
}
