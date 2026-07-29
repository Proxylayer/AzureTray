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
    [InlineData(3)]
    [InlineData(4)]
    public void IsSupported_AcceptsKnownShippedVersions(int apiVersion)
    {
        // Every shipped API version must still load — the contract surface has
        // only ever evolved additively (API 4 added
        // IPluginContext.RefreshTokenAsync as a default-implemented member).
        Assert.True(PluginApiVersion.IsSupported(apiVersion));
    }

    // Pinned deliberately: bumping Current is an intentional act that should
    // come with a contract change, and raising MinSupported locks out shipped
    // plugins, so both values are asserted rather than derived.
    [Fact]
    public void Current_IsFour_AndMinSupportedIsStillOne()
    {
        Assert.Equal(4, PluginApiVersion.Current);
        Assert.Equal(1, PluginApiVersion.MinSupported);
    }
}
