using System;
using System.Linq;
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

        // Poll for the clear rather than outwaiting the worst case: this returns
        // as soon as the 100 ms timer fires, and still tolerates a loaded
        // machine taking far longer than a fixed margin would have allowed.
        await Settle.UntilAsync(() => Cleared(clipboard) >= 1, TimeSpan.FromSeconds(5));

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

        // Long delay so it can't fire on its own within the test window. It only
        // has to outlast the wait below, and the wait is bounded, so seconds of
        // headroom buy nothing that milliseconds do not.
        plugin.ClipboardClearDelayOverride = TimeSpan.FromMilliseconds(500);
        plugin.ScheduleClipboardClear("hunter2");

        // Shutdown cancels the lifetime token the pending clear awaits on.
        await plugin.ShutdownAsync(CancellationToken.None);

        // Proving an absence has no condition to poll for, so this is a plain
        // bounded wait - long enough for a cancelled clear to have surfaced.
        await Settle.QuietAsync();

        clipboard.DidNotReceive().ClearIfMatches(Arg.Any<string>());
    }

    // A poll condition for the scheduled clear. NSubstitute's Received(1)
    // asserts instead of reporting, so it cannot be used to wait on.
    private static int Cleared(IClipboard clipboard)
        => clipboard.ReceivedCalls().Count(c =>
            c.GetMethodInfo().Name == nameof(IClipboard.ClearIfMatches));

    private static IPluginContext NewContext(IClipboard clipboard)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.Logger.Returns(NullLogger.Instance);
        ctx.ReadyTenants.Returns(Array.Empty<PluginTenant>());
        ctx.Clipboard.Returns(clipboard);
        return ctx;
    }
}
