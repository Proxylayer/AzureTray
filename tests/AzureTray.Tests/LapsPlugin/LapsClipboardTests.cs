using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugin.LAPS;
using Xunit;

namespace AzureTray.Tests.LapsPlugin;

// Exercises the clipboard auto-expiry path with a sub-second delay so we can
// confirm the scheduled clear actually fires (and respects the option /
// shutdown) without waiting the real 5 minutes.
public sealed class LapsClipboardTests
{
    private const string ClearClipboardOptionKey = "clearClipboardAfterCopy";
    private const string ClearAfterMinutesOptionKey = "clearClipboardAfterMinutes";

    [Fact]
    public void ClearClipboardAfterCopy_DefaultsOn_AndRespectsToggle()
    {
        var plugin = new AzureTray.Plugin.LAPS.LapsPlugin();

        Assert.True(plugin.ClearClipboardAfterCopy);          // default on

        plugin.SetValue(ClearClipboardOptionKey, false);
        Assert.False(plugin.ClearClipboardAfterCopy);          // unchecked → never expires

        plugin.SetValue(ClearClipboardOptionKey, true);
        Assert.True(plugin.ClearClipboardAfterCopy);
    }

    [Fact]
    public void ClearClipboardMinutes_DefaultsToFive_AndTracksTheOption()
    {
        var plugin = new AzureTray.Plugin.LAPS.LapsPlugin();

        Assert.Equal(5, plugin.ClearClipboardMinutes);                 // default
        Assert.Equal(TimeSpan.FromMinutes(5), plugin.ClipboardClearDelay);

        plugin.SetValue(ClearAfterMinutesOptionKey, 15);
        Assert.Equal(15, plugin.ClearClipboardMinutes);
        Assert.Equal(TimeSpan.FromMinutes(15), plugin.ClipboardClearDelay);

        // Non-positive / nonsense falls back to the default rather than
        // disabling expiry or firing instantly.
        plugin.SetValue(ClearAfterMinutesOptionKey, 0);
        Assert.Equal(5, plugin.ClearClipboardMinutes);
    }

    [Fact]
    public async Task ScheduleClipboardClear_AfterDelay_ClearsTheCopiedPassword()
    {
        var clipboard = Substitute.For<IClipboard>();
        var plugin = new AzureTray.Plugin.LAPS.LapsPlugin();
        await plugin.InitializeAsync(NewContext(clipboard), CancellationToken.None);

        plugin.ClipboardClearDelayOverride = TimeSpan.FromMilliseconds(100);
        plugin.ScheduleClipboardClear("hunter2");

        // Generous margin over the 100 ms delay; the timer fires well before.
        await Task.Delay(TimeSpan.FromMilliseconds(600));

        // ClearIfMatches only wipes if the value is still ours, so passing the
        // exact password is what makes the expiry safe (no clobbering).
        clipboard.Received(1).ClearIfMatches("hunter2");

        await plugin.ShutdownAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ScheduleClipboardClear_CancelledByShutdown_DoesNotClear()
    {
        var clipboard = Substitute.For<IClipboard>();
        var plugin = new AzureTray.Plugin.LAPS.LapsPlugin();
        await plugin.InitializeAsync(NewContext(clipboard), CancellationToken.None);

        // Long delay so it can't fire on its own within the test window.
        plugin.ClipboardClearDelayOverride = TimeSpan.FromSeconds(5);
        plugin.ScheduleClipboardClear("hunter2");

        // Shutdown cancels the lifetime token the pending clear awaits on.
        await plugin.ShutdownAsync(CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(300));

        clipboard.DidNotReceive().ClearIfMatches(Arg.Any<string>());
    }

    private static IPluginContext NewContext(IClipboard clipboard)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.ReadyTenants.Returns(Array.Empty<PluginTenant>());
        ctx.Clipboard.Returns(clipboard);
        return ctx;
    }
}
