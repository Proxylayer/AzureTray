using System;
using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray;
using AzureTray.Configuration;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The two user-owned plugin-update choices. Auto-update defaults OFF and must
// stay off through every degraded case — a corrupt preferences file silently
// enabling unattended installs would be a security regression, not a bug.
public sealed class PluginUpdatePreferenceStoreTests : IDisposable
{
    private const string FileName = "plugin-updates.json";

    private readonly string _root;
    private readonly string _configDir;
    private readonly IAppPaths _paths;

    public PluginUpdatePreferenceStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.PluginPrefs", Guid.NewGuid().ToString("N"));
        _configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(_configDir);

        _paths = Substitute.For<IAppPaths>();
        _paths.ConfigDir.Returns(_configDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // Best-effort.
        }
    }

    [Fact]
    public void WithNoFile_AutoUpdateIsOff()
    {
        var store = NewStore();

        Assert.False(store.AutoUpdateEnabled);
    }

    [Fact]
    public void WithNoFile_PrereleaseSeedsFromConfig()
    {
        Assert.True(NewStore(includePrereleaseByDefault: true).IncludePrerelease);
        Assert.False(NewStore(includePrereleaseByDefault: false).IncludePrerelease);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{ \"autoUpdate\": \"yes please\" }")]
    public void WithACorruptFile_FallsBackToDefaultsWithAutoUpdateOff(string contents)
    {
        File.WriteAllText(Path.Combine(_configDir, FileName), contents);

        var store = NewStore(includePrereleaseByDefault: true);

        Assert.False(store.AutoUpdateEnabled);
        Assert.True(store.IncludePrerelease);
    }

    [Fact]
    public void WithAConfigDefaultOfOn_TheConfiguredValueSeedsTheCheckbox()
    {
        // App:Plugins:AutoUpdate is the admin-set seed; the shipped default is
        // false (asserted here so a flipped default breaks a test).
        Assert.False(new PluginOptions().AutoUpdate);
        Assert.True(NewStore(autoUpdateDefault: true).AutoUpdateEnabled);
    }

    [Fact]
    public void SettingsRoundTripThroughDiskToTheNextInstance()
    {
        var store = NewStore(includePrereleaseByDefault: true);
        store.AutoUpdateEnabled = true;
        store.IncludePrerelease = false;

        Assert.True(File.Exists(Path.Combine(_configDir, FileName)));

        // Fresh instance with the OPPOSITE config defaults: the persisted user
        // choice has to win over both.
        var reloaded = NewStore(includePrereleaseByDefault: true, autoUpdateDefault: false);

        Assert.True(reloaded.AutoUpdateEnabled);
        Assert.False(reloaded.IncludePrerelease);
    }

    [Fact]
    public void AWriteIsVisibleToASubsequentRead()
    {
        var store = NewStore();
        Assert.False(store.AutoUpdateEnabled);

        store.AutoUpdateEnabled = true;

        Assert.True(store.AutoUpdateEnabled);
        Assert.True(NewStore().AutoUpdateEnabled);

        store.AutoUpdateEnabled = false;

        Assert.False(NewStore().AutoUpdateEnabled);
    }

    [Fact]
    public void APartialFileOnlyOverridesWhatItCarries()
    {
        File.WriteAllText(Path.Combine(_configDir, FileName), "{ \"includePrerelease\": false }");

        var store = NewStore(includePrereleaseByDefault: true, autoUpdateDefault: true);

        Assert.False(store.IncludePrerelease);
        Assert.True(store.AutoUpdateEnabled);
    }

    [Fact]
    public void PersistCreatesTheConfigDirectoryWhenMissing()
    {
        Directory.Delete(_configDir, recursive: true);
        var store = NewStore();

        store.IncludePrerelease = !store.IncludePrerelease;

        Assert.True(File.Exists(Path.Combine(_configDir, FileName)));
        // Nothing written outside the substituted config root.
        Assert.Equal(
            new[] { Path.Combine(_configDir, FileName) },
            Directory.GetFiles(_root, "*", SearchOption.AllDirectories));
    }

    private PluginUpdatePreferenceStore NewStore(
        bool includePrereleaseByDefault = false,
        bool autoUpdateDefault = false)
        => new(
            _paths,
            Options.Create(new NuGetPluginFeedOptions { IncludePrereleaseByDefault = includePrereleaseByDefault }),
            Options.Create(new PluginOptions { AutoUpdate = autoUpdateDefault }),
            NullLogger<PluginUpdatePreferenceStore>.Instance);
}
