using System.Threading;
using System.Threading.Tasks;
using AzureTray.AppRegistration;
using Xunit;
using static AzureTray.Tests.AppRegistration.AppRegistrationTestFixtures;

namespace AzureTray.Tests.AppRegistration;

public sealed class AppRegistrationDiscoveryTests
{
    [Fact]
    public async Task FindByDisplayNameAsync_ReturnsInfo_WhenFound()
    {
        var handler = new RoutedHttpHandler();
        handler.OnGet("https://graph.microsoft.com/v1.0/applications", _ => Json("""
            { "value": [ { "id": "app-obj-1", "appId": "client-1", "displayName": "Test App" } ] }
            """));

        var discovery = new AppRegistrationDiscovery(NewGraphClient(handler));

        var result = await discovery.FindByDisplayNameAsync("tenant-1", "Test App", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("app-obj-1", result!.ObjectId);
        Assert.Equal("client-1", result.AppId);
        Assert.Equal("Test App", result.DisplayName);
    }

    [Fact]
    public async Task FindByDisplayNameAsync_ReturnsNull_WhenEmpty()
    {
        var handler = new RoutedHttpHandler();
        handler.OnGet("https://graph.microsoft.com/v1.0/applications", _ => Json(@"{ ""value"": [] }"));

        var discovery = new AppRegistrationDiscovery(NewGraphClient(handler));

        var result = await discovery.FindByDisplayNameAsync("tenant-1", "Missing", CancellationToken.None);
        Assert.Null(result);
    }
}
