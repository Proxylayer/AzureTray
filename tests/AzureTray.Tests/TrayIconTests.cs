using System;
using Xunit;

namespace AzureTray.Tests;

public sealed class TrayIconTests
{
    private static readonly DateTime MouseClick = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsMouseClickEcho_InsideWindow_True()
    {
        Assert.True(TrayIcon.IsMouseClickEcho(MouseClick, MouseClick.AddMilliseconds(100)));
    }

    [Fact]
    public void IsMouseClickEcho_SameInstant_True()
    {
        Assert.True(TrayIcon.IsMouseClickEcho(MouseClick, MouseClick));
    }

    [Fact]
    public void IsMouseClickEcho_JustUnderWindow_True()
    {
        Assert.True(TrayIcon.IsMouseClickEcho(MouseClick, MouseClick.AddMilliseconds(249)));
    }

    [Fact]
    public void IsMouseClickEcho_ExactlyAtWindow_False()
    {
        // Pinned: the 250ms echo window is exclusive (<) at the boundary.
        Assert.False(TrayIcon.IsMouseClickEcho(MouseClick, MouseClick.AddMilliseconds(250)));
    }

    [Fact]
    public void IsMouseClickEcho_WellOutsideWindow_False()
    {
        Assert.False(TrayIcon.IsMouseClickEcho(MouseClick, MouseClick.AddSeconds(2)));
    }

    [Fact]
    public void IsMouseClickEcho_NeverClicked_DefaultTimestamp_False()
    {
        // default(DateTime) is the "no mouse click yet" state in TrayIcon.
        Assert.False(TrayIcon.IsMouseClickEcho(default, MouseClick));
    }
}
