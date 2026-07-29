using System;
using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// FormatRemaining renders the countdown baked into an active role's menu row.
// Offsets carry a few extra seconds so the label can't flip a minute down
// between building the input and evaluating it.
public sealed class EligibleRolesWatcherFormatRemainingTests
{
    [Theory]
    [InlineData(3 * 3600 + 42 * 60 + 5, "3h 42m left")]
    [InlineData(3600 + 5, "1h left")]
    [InlineData(47 * 60 + 5, "47m left")]
    [InlineData(60 + 5, "1m left")]
    [InlineData(30, "< 1m left")]
    [InlineData(1, "< 1m left")]
    [InlineData(2 * 86400 + 5, "2d left")]
    public void FormatRemaining_RendersExpectedLabel(int secondsFromNow, string expected)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(secondsFromNow);

        Assert.Equal(expected, EligibleRolesWatcher.FormatRemaining(end));
    }

    // An end time that has already passed must never render a negative
    // duration — null tells the caller to fall back to the bare marker.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-3600)]
    [InlineData(-30 * 86400)]
    public void FormatRemaining_ReturnsNull_WhenEndHasPassed(int secondsFromNow)
    {
        var end = DateTimeOffset.UtcNow.AddSeconds(secondsFromNow);

        Assert.Null(EligibleRolesWatcher.FormatRemaining(end));
    }

    [Fact]
    public void FormatRemaining_NeverRendersANegativeDuration()
    {
        var label = EligibleRolesWatcher.FormatRemaining(DateTimeOffset.UtcNow.AddHours(-4));

        Assert.Null(label);
    }
}
