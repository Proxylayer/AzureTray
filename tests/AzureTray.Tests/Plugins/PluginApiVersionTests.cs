using AzureTray.Plugin.Contracts;
using Xunit;

namespace AzureTray.Tests.Plugins;

public sealed class PluginApiVersionTests
{
    [Fact]
    public void Range_IsWellFormed()
    {
        Assert.True(PluginApiVersion.MinSupported <= PluginApiVersion.Current);
    }

    [Fact]
    public void IsSupported_AcceptsBothEndsOfRange()
    {
        Assert.True(PluginApiVersion.IsSupported(PluginApiVersion.MinSupported));
        Assert.True(PluginApiVersion.IsSupported(PluginApiVersion.Current));
    }

    [Fact]
    public void IsSupported_RejectsBelowMinSupported()
    {
        Assert.False(PluginApiVersion.IsSupported(PluginApiVersion.MinSupported - 1));
    }

    [Fact]
    public void IsSupported_RejectsAboveCurrent()
    {
        Assert.False(PluginApiVersion.IsSupported(PluginApiVersion.Current + 1));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void IsSupported_AcceptsKnownShippedVersions(int apiVersion)
    {
        // API 1 (initial) and API 2 (current) plugins must both load — the
        // contract surface evolved additively between them.
        Assert.True(PluginApiVersion.IsSupported(apiVersion));
    }
}
