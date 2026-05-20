using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.PIM;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Dto;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

public sealed class PimPluginTests
{
    [Fact]
    public void GetMenuItems_BeforeInit_ReturnsEmpty()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        Assert.Empty(plugin.GetMenuItems());
    }

    [Fact]
    public async Task GetMenuItems_AfterInit_BuildsBothTopLevelMenus()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        var context = NewContext(
            new PluginTenant("tenant-1", "Contoso"),
            new PluginTenant("tenant-2", "Fabrikam"));

        await plugin.InitializeAsync(context, CancellationToken.None);
        try
        {
            var items = plugin.GetMenuItems();

            Assert.Equal(2, items.Count);

            var pending = items[0];
            Assert.StartsWith("⏳", pending.Text, StringComparison.Ordinal);
            Assert.Contains("Pending Approvals", pending.Text, StringComparison.Ordinal);
            Assert.NotNull(pending.Children);
            // Refresh rows carry the tenant name in Text and the spinner
            // glyph in Icon (a separate field the host animates).
            Assert.Contains(pending.Children!, c => c.Text == "Contoso" && c.Icon == "↻" && c.Invoke is not null);
            Assert.Contains(pending.Children!, c => c.Text == "Fabrikam" && c.Icon == "↻" && c.Invoke is not null);
            Assert.Contains(pending.Children!, c => c.Text == "    (none)" && !c.IsEnabled);
            Assert.Contains(pending.Children!, c => c.IsSeparator);

            var openRequest = items[1];
            Assert.StartsWith("🔑", openRequest.Text, StringComparison.Ordinal);
            Assert.Contains("Open Request", openRequest.Text, StringComparison.Ordinal);
            Assert.NotNull(openRequest.Children);
            Assert.Contains(openRequest.Children!, c => c.Text == "Contoso" && c.Icon == "↻" && c.Invoke is not null);
            Assert.Contains(openRequest.Children!, c => c.Text == "Fabrikam" && c.Icon == "↻" && c.Invoke is not null);
            Assert.Contains(openRequest.Children!, c => c.Text == "    (none)" && !c.IsEnabled);
        }
        finally
        {
            await plugin.ShutdownAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetMenuItems_AfterShutdown_ReturnsEmpty()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        var context = NewContext(new PluginTenant("tenant-1", "Contoso"));

        await plugin.InitializeAsync(context, CancellationToken.None);
        await plugin.ShutdownAsync(CancellationToken.None);

        Assert.Empty(plugin.GetMenuItems());
    }

    [Fact]
    public async Task RequiredPermissions_DeclaresPimScopes()
    {
        using var plugin = new AzureTray.Plugin.PIM.PimPlugin();
        var permissions = plugin.RequiredPermissions;

        Assert.Contains(permissions, p => p.ScopeName == "User.Read");
        Assert.Contains(permissions, p => p.ScopeName == "RoleAssignmentSchedule.ReadWrite.Directory");
        Assert.Contains(permissions, p => p.ScopeName == "user_impersonation" && p.Api == PermissionApi.AzureResourceManager);
        await Task.CompletedTask;
    }

    private static IPluginContext NewContext(params PluginTenant[] tenants)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger<PimPluginTests>.Instance);
        ctx.Tenants.Returns(tenants);
        // The plugin now starts watchers in response to ReadyTenants /
        // TenantReady. Treat every test tenant as already ready so existing
        // assertions about menu shape continue to hold.
        ctx.ReadyTenants.Returns(tenants);

        var http = Substitute.For<IPluginHttpClient>();
        ctx.GetHttpClient(Arg.Any<string>()).Returns(http);

        var notifier = Substitute.For<INotifier>();
        ctx.Notifier.Returns(notifier);

        ctx.GraphScope.Returns("https://graph.microsoft.com/.default");
        ctx.ArmScope.Returns("https://management.azure.com/.default");

        return ctx;
    }
}
