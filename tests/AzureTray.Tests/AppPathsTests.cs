using System;
using System.IO;
using AzureTray;
using Xunit;

namespace AzureTray.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _localRoot;
    private readonly string _roamingRoot;

    public AppPathsTests()
    {
        _localRoot = Path.Combine(Path.GetTempPath(), "AzureTray.Tests", Guid.NewGuid().ToString("N"), "local");
        _roamingRoot = Path.Combine(Path.GetTempPath(), "AzureTray.Tests", Guid.NewGuid().ToString("N"), "roaming");
    }

    public void Dispose()
    {
        TryDelete(_localRoot);
        TryDelete(_roamingRoot);
    }

    [Fact]
    public void Constructor_WithCustomRoots_BuildsExpectedPaths()
    {
        var paths = new AppPaths(_localRoot, _roamingRoot);

        Assert.Equal(Path.Combine(_localRoot, "AzureTray.Data"), paths.DataDir);
        Assert.Equal(Path.Combine(_localRoot, "AzureTray.Data", "logs"), paths.LogsDir);
        Assert.Equal(Path.Combine(_localRoot, "AzureTray.Data", "plugins"), paths.PluginsDir);
        Assert.Equal(Path.Combine(_localRoot, "AzureTray.Data", "logs", "app-.log"), paths.LogFileTemplate);
        Assert.Equal(Path.Combine(_roamingRoot, "AzureTray"), paths.ConfigDir);
        Assert.Equal(Path.Combine(_roamingRoot, "AzureTray", "config.json"), paths.UserConfigFilePath);
        Assert.Equal(Path.Combine(_roamingRoot, "AzureTray", "tenants.json"), paths.TenantStoreFilePath);
    }

    [Fact]
    public void EnsureDirectoriesExist_CreatesAllDirectories()
    {
        var paths = new AppPaths(_localRoot, _roamingRoot);

        paths.EnsureDirectoriesExist();

        Assert.True(Directory.Exists(paths.ConfigDir));
        Assert.True(Directory.Exists(paths.DataDir));
        Assert.True(Directory.Exists(paths.LogsDir));
        Assert.True(Directory.Exists(paths.PluginsDir));
    }

    [Fact]
    public void Constructor_WithNullOrBlankRoot_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(null!, _roamingRoot));
        Assert.ThrowsAny<ArgumentException>(() => new AppPaths(_localRoot, "   "));
    }

    [Fact]
    public void DataDir_IsSiblingOfVelopackInstallRoot_NotChild()
    {
        // The Velopack install lives at %LOCALAPPDATA%\AzureTray\. Our data dir
        // must NOT be a subfolder of that path or Velopack could wipe it on update.
        var paths = new AppPaths(_localRoot, _roamingRoot);
        var velopackRoot = Path.Combine(_localRoot, "AzureTray");

        Assert.False(paths.DataDir.StartsWith(velopackRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(paths.LogsDir.StartsWith(velopackRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
        Assert.False(paths.PluginsDir.StartsWith(velopackRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; do not fail tests on cleanup errors.
        }
    }
}
