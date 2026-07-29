using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using AzureTray;
using AzureTray.Extensions;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The install manifest is the ONLY on-disk record of a plugin's version, so
// every read has to degrade to "unknown" (null) instead of throwing: a
// hand-mangled JSON file must not break the Settings window or the poll loop.
public sealed class PluginManifestStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly IAppPaths _paths;

    public PluginManifestStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.Manifest", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_pluginsDir);

        _paths = Substitute.For<IAppPaths>();
        _paths.PluginsDir.Returns(_pluginsDir);
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
    public void WriteThenTryRead_RoundTripsEveryField()
    {
        var store = NewStore();
        var installedUtc = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);

        store.Write(new InstalledPluginManifest("Acme.Plugin.Foo", "1.2.3", installedUtc)
        {
            PluginId = "com.acme.foo",
            SourceUrl = "https://nuget/acme.plugin.foo.1.2.3.nupkg",
        });

        var read = store.TryRead("Acme.Plugin.Foo");

        Assert.NotNull(read);
        Assert.Equal("Acme.Plugin.Foo", read!.PackageId);
        Assert.Equal("1.2.3", read.Version);
        Assert.Equal(installedUtc, read.InstalledUtc);
        Assert.Equal("com.acme.foo", read.PluginId);
        Assert.Equal("https://nuget/acme.plugin.foo.1.2.3.nupkg", read.SourceUrl);
    }

    [Fact]
    public void Write_LandsAtPluginsDirPackageIdManifestJson_AndNowhereElse()
    {
        var store = NewStore();

        store.Write(new InstalledPluginManifest("Acme.Plugin.Foo", "1.2.3", DateTimeOffset.UtcNow));

        var expected = Path.Combine(_pluginsDir, "Acme.Plugin.Foo", "azuretray-plugin.json");
        Assert.True(File.Exists(expected), $"expected manifest at {expected}");
        Assert.Equal("azuretray-plugin.json", PluginManifestStore.FileName);

        // Nothing outside the substituted plugins root: the only file anywhere
        // under the temp root is the manifest we asked for.
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).ToArray();
        Assert.Equal(new[] { expected }, files);
    }

    [Fact]
    public void Write_OverwritesAPreviousManifest()
    {
        var store = NewStore();
        store.Write(new InstalledPluginManifest("Acme.Plugin.Foo", "1.0.0", DateTimeOffset.UtcNow));

        store.Write(new InstalledPluginManifest("Acme.Plugin.Foo", "2.0.0", DateTimeOffset.UtcNow));

        // An in-place version bump must not leave the old version on disk.
        Assert.Equal("2.0.0", store.TryRead("Acme.Plugin.Foo")!.Version);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(_pluginsDir, "Acme.Plugin.Foo")));
    }

    [Fact]
    public void Write_DoesNotThrowWhenTheManifestCannotBeWritten()
    {
        // A file where the plugin folder should be makes CreateDirectory fail.
        File.WriteAllText(Path.Combine(_pluginsDir, "Blocked"), string.Empty);
        var store = NewStore();

        store.Write(new InstalledPluginManifest("Blocked", "1.0.0", DateTimeOffset.UtcNow));

        Assert.Null(store.TryRead("Blocked"));
    }

    [Fact]
    public void Write_ThrowsOnNullManifest()
        => Assert.Throws<ArgumentNullException>(() => NewStore().Write(null!));

    [Fact]
    public void TryRead_ReturnsNullWhenTheFileIsMissing()
    {
        var store = NewStore();

        Assert.Null(store.TryRead("Never.Installed"));
    }

    [Theory]
    [InlineData("{ not json at all")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{ \"packageId\": \"Acme.Plugin.Foo\" }")]                                 // no version
    [InlineData("{ \"version\": \"1.0.0\" }")]                                             // no packageId
    [InlineData("{ \"packageId\": \"   \", \"version\": \"1.0.0\" }")]                     // blank packageId
    [InlineData("{ \"packageId\": \"Acme.Plugin.Foo\", \"version\": \"   \" }")]           // blank version
    [InlineData("{ \"packageId\": null, \"version\": null }")]
    public void TryRead_DegradesToNullWithoutThrowing(string contents)
    {
        WriteRawManifest("Acme.Plugin.Foo", contents);
        var store = NewStore();

        Assert.Null(store.TryRead("Acme.Plugin.Foo"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRead_ReturnsNullForABlankPackageId(string? packageId)
        => Assert.Null(NewStore().TryRead(packageId!));

    [Fact]
    public void TryReadForDll_ReadsTheManifestBesideTheDll()
    {
        var store = NewStore();
        store.Write(new InstalledPluginManifest("Acme.Plugin.Foo", "1.2.3", DateTimeOffset.UtcNow));
        var dll = Path.Combine(_pluginsDir, "Acme.Plugin.Foo", "Acme.Plugin.Foo.dll");

        var read = store.TryReadForDll(dll);

        Assert.NotNull(read);
        Assert.Equal("1.2.3", read!.Version);
    }

    [Fact]
    public void TryReadForDll_ReturnsNullForTheLegacyTopLevelLayout()
    {
        // plugins/azuretray-plugin.json would otherwise be shared by every
        // legacy top-level DLL, so that layout has no manifest at all.
        File.WriteAllText(
            Path.Combine(_pluginsDir, PluginManifestStore.FileName),
            "{ \"packageId\": \"Legacy\", \"version\": \"9.9.9\" }");
        var store = NewStore();

        Assert.Null(store.TryReadForDll(Path.Combine(_pluginsDir, "Legacy.dll")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryReadForDll_ReturnsNullForABlankPath(string? dllPath)
        => Assert.Null(NewStore().TryReadForDll(dllPath!));

    [Fact]
    public void TryReadForDll_ReturnsNullWhenTheFolderHasNoManifest()
    {
        var dir = Path.Combine(_pluginsDir, "Acme.Plugin.Bare");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "Acme.Plugin.Bare.dll"), new byte[] { 0x4D, 0x5A });

        Assert.Null(NewStore().TryReadForDll(Path.Combine(dir, "Acme.Plugin.Bare.dll")));
    }

    private PluginManifestStore NewStore()
        => new(_paths, NullLogger<PluginManifestStore>.Instance);

    private void WriteRawManifest(string packageId, string contents)
    {
        var dir = Path.Combine(_pluginsDir, packageId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, PluginManifestStore.FileName), contents);
    }
}
