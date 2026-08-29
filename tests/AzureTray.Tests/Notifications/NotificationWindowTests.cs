using System;
using AzureTray.Notifications;
using Xunit;

namespace AzureTray.Tests.Notifications;

public sealed class NotificationWindowTests
{
    private static readonly DateTime Shown = new(2026, 8, 28, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsEnterArmed_NullShownAt_False()
    {
        Assert.False(NotificationWindow.IsEnterArmed(null, Shown));
    }

    [Fact]
    public void IsEnterArmed_JustUnderThreshold_False()
    {
        Assert.False(NotificationWindow.IsEnterArmed(Shown, Shown.AddMilliseconds(299)));
    }

    [Fact]
    public void IsEnterArmed_ExactlyAtThreshold_True()
    {
        // Pinned: the 300ms arming delay is inclusive (>=) at the boundary.
        Assert.True(NotificationWindow.IsEnterArmed(Shown, Shown.AddMilliseconds(300)));
    }

    [Fact]
    public void IsEnterArmed_WellPastThreshold_True()
    {
        Assert.True(NotificationWindow.IsEnterArmed(Shown, Shown.AddSeconds(5)));
    }

    [Fact]
    public void IsEnterArmed_ZeroElapsed_False()
    {
        Assert.False(NotificationWindow.IsEnterArmed(Shown, Shown));
    }
}
