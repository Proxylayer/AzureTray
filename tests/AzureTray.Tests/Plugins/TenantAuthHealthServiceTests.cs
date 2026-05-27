using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Tenants;
using Xunit;

namespace AzureTray.Tests.Plugins;

public sealed class TenantAuthHealthServiceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void ReportFailure_OnReadyTenant_FlagsTenant_FiresEvent_ShowsPopup()
    {
        var notifier = Substitute.For<INotifier>();
        // Popup stays "open" so the state machine treats a notification as active.
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<NotificationResult>().Task);

        var tracker = new TenantReadinessTracker();
        tracker.MarkReady(new PluginTenant(TenantId, "Contoso"));
        var svc = NewService(notifier, tracker);

        var events = 0;
        string? lastId = null;
        svc.AuthStateChanged += id => { events++; lastId = id; };

        svc.ReportFailure(TenantId);

        Assert.True(svc.NeedsReauth(TenantId));
        Assert.Equal(1, events);
        Assert.Equal(TenantId, lastId);
        Assert.Contains(TenantId, svc.FailedTenants);
        notifier.Received(1).ShowAsync(Arg.Any<ActionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReportFailure_Twice_DeduplicatesPopupAndEvent()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<NotificationResult>().Task);

        var tracker = new TenantReadinessTracker();
        tracker.MarkReady(new PluginTenant(TenantId, "Contoso"));
        var svc = NewService(notifier, tracker);

        var events = 0;
        svc.AuthStateChanged += _ => events++;

        svc.ReportFailure(TenantId);
        svc.ReportFailure(TenantId);

        Assert.Equal(1, events);
        notifier.Received(1).ShowAsync(Arg.Any<ActionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Failure_AfterUserDismiss_ReRaisesPopup()
    {
        var notifier = Substitute.For<INotifier>();
        // Completed-immediately Dismiss result: the user closed it without resolving.
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<NotificationResult>(new DismissedResult()));

        var tracker = new TenantReadinessTracker();
        tracker.MarkReady(new PluginTenant(TenantId, "Contoso"));
        var svc = NewService(notifier, tracker);

        svc.ReportFailure(TenantId); // shows + user dismisses synchronously
        Assert.True(svc.NeedsReauth(TenantId)); // still failed after dismiss

        svc.ReportFailure(TenantId); // re-raises because no popup is active

        notifier.Received(2).ShowAsync(Arg.Any<ActionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ReportRecovered_ClearsStateAndFiresEvent()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<NotificationResult>().Task);

        var tracker = new TenantReadinessTracker();
        tracker.MarkReady(new PluginTenant(TenantId, "Contoso"));
        var svc = NewService(notifier, tracker);

        var events = 0;
        svc.AuthStateChanged += _ => events++;

        svc.ReportFailure(TenantId);
        Assert.True(svc.NeedsReauth(TenantId));

        svc.ReportRecovered(TenantId);

        Assert.False(svc.NeedsReauth(TenantId));
        Assert.DoesNotContain(TenantId, svc.FailedTenants);
        Assert.Equal(2, events); // healthy->failed, then failed->healthy
    }

    [Fact]
    public void ReportRecovered_WhenNotFailed_DoesNothing()
    {
        var notifier = Substitute.For<INotifier>();
        var tracker = new TenantReadinessTracker();
        tracker.MarkReady(new PluginTenant(TenantId, "Contoso"));
        var svc = NewService(notifier, tracker);

        var events = 0;
        svc.AuthStateChanged += _ => events++;

        svc.ReportRecovered(TenantId);

        Assert.False(svc.NeedsReauth(TenantId));
        Assert.Equal(0, events);
    }

    [Fact]
    public void ReportFailure_OnNotReadyTenant_IsIgnored()
    {
        var notifier = Substitute.For<INotifier>();
        var tracker = new TenantReadinessTracker(); // tenant never marked ready
        var svc = NewService(notifier, tracker);

        var events = 0;
        svc.AuthStateChanged += _ => events++;

        svc.ReportFailure(TenantId);

        Assert.False(svc.NeedsReauth(TenantId));
        Assert.Equal(0, events);
        notifier.DidNotReceive().ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TryResolveAsync_Success_ClearsState()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<NotificationResult>().Task);

        var credentials = Substitute.For<ICredentialFactory>();
        var tracker = new TenantReadinessTracker();
        tracker.MarkReady(new PluginTenant(TenantId, "Contoso"));
        var svc = NewService(notifier, tracker, credentials);

        svc.ReportFailure(TenantId);
        Assert.True(svc.NeedsReauth(TenantId));

        var ok = await svc.TryResolveAsync(TenantId, CancellationToken.None);

        Assert.True(ok);
        Assert.False(svc.NeedsReauth(TenantId));
        await credentials.Received(1).SignInAsync(TenantId, Arg.Any<CancellationToken>());
    }

    private static TenantAuthHealthService NewService(
        INotifier notifier,
        ITenantReadinessTracker tracker,
        ICredentialFactory? credentials = null)
    {
        var store = Substitute.For<ITenantStore>();
        store.FindByTenantId(TenantId).Returns(new Tenant(TenantId, "Contoso", null));

        credentials ??= Substitute.For<ICredentialFactory>();

        var cloud = Substitute.For<IAzureCloudConfig>();
        cloud.GraphScope.Returns("https://graph.microsoft.com/.default");

        return new TenantAuthHealthService(
            store,
            credentials,
            tracker,
            cloud,
            notifier,
            Options.Create(new AuthOptions()),
            NullLogger<TenantAuthHealthService>.Instance);
    }
}
