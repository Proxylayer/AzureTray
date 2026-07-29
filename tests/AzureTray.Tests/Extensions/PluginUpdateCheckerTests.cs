using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using Xunit;

namespace AzureTray.Tests.Extensions;

// Installed index → feed → parsed comparison. The hard rule is that a version
// we can't parse on either side is never treated as an update: guessing there
// would auto-install packages on nothing more than a string mismatch.
public sealed class PluginUpdateCheckerTests : IDisposable
{
    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly string _configDir;
    private readonly IAppPaths _paths;
    private readonly PluginManifestStore _manifests;

    public PluginUpdateCheckerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.UpdateChecker", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        _configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(_pluginsDir);
        Directory.CreateDirectory(_configDir);

        _paths = Substitute.For<IAppPaths>();
        _paths.PluginsDir.Returns(_pluginsDir);
        _paths.ConfigDir.Returns(_configDir);
        _manifests = new PluginManifestStore(_paths, NullLogger<PluginManifestStore>.Instance);
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
    public async Task CheckAsync_ReportsAnUpdateWhenTheFeedIsNewer()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "0.8.0");
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "0.8.0", "0.9.0")]);

        var updates = await checker.CheckAsync(CancellationToken.None);

        var update = Assert.Single(updates);
        Assert.Equal("Acme.Plugin.Foo", update.PackageId);
        Assert.Equal("0.8.0", update.InstalledVersion);
        Assert.Equal("0.9.0", update.LatestVersion);
        Assert.Equal(dll, update.InstalledDllPath);
        Assert.Equal("https://nuget/Acme.Plugin.Foo/0.9.0.nupkg", update.DownloadUrl);
        Assert.Equal("Foo Plugin  0.8.0 → 0.9.0", update.SummaryLine);
    }

    [Fact]
    public async Task CheckAsync_ReportsNothingWhenTheFeedMatchesTheInstalledVersion()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "1.0.0");
        var checker = NewChecker(installedDlls: [dll], feed: [Entry("Acme.Plugin.Foo", "1.0.0")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_ReportsNothingWhenTheFeedIsOlderThanWhatIsInstalled()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "2.0.0");
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "1.0.0", "1.9.9")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_IgnoresBuildMetadataOnlyDifferences()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "1.0.0+abc");
        var checker = NewChecker(installedDlls: [dll], feed: [Entry("Acme.Plugin.Foo", "1.0.0+def")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("latest")]
    [InlineData("v1.0.0")]
    public async Task CheckAsync_NeverReportsAnUpdateForAnUnparseableInstalledVersion(string installedVersion)
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", installedVersion);
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "9.9.9")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_NeverReportsAnUpdateForAnUnparseableFeedVersion()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "1.0.0");
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "garbage", "also-garbage")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_PrereleaseIsAnUpdateOnlyWhenPrereleasesAreOptedIn()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "1.0.0");

        var optedOut = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "1.0.0", "1.1.0-beta.1")],
            includePrerelease: false);
        Assert.Empty(await optedOut.CheckAsync(CancellationToken.None));

        var optedIn = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "1.0.0", "1.1.0-beta.1")],
            includePrerelease: true);
        Assert.Equal("1.1.0-beta.1", Assert.Single(await optedIn.CheckAsync(CancellationToken.None)).LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_PassesThePreferenceThroughToTheFeedQuery()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "1.0.0");
        var feed = Substitute.For<INuGetPluginFeed>();
        feed.FetchAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns((IReadOnlyList<NuGetPluginEntry>)Array.Empty<NuGetPluginEntry>());

        var checker = NewChecker(installedDlls: [dll], feedSubstitute: feed, includePrerelease: true);
        await checker.CheckAsync(CancellationToken.None);

        // Same arguments the Settings browse list uses, so the poll refreshes
        // the feed cache slot instead of evicting it.
        await feed.Received().FetchAsync(null, true, Arg.Any<CancellationToken>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task CheckAsync_FallsBackToTheLoadedVersionWhenThereIsNoManifest()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", manifestVersion: null);
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "0.9.0")],
            loaded: [Loaded(dll, "com.acme.foo", "0.8.0")]);

        var update = Assert.Single(await checker.CheckAsync(CancellationToken.None));
        Assert.Equal("0.8.0", update.InstalledVersion);
        Assert.Equal("0.9.0", update.LatestVersion);
        Assert.Equal("com.acme.foo", update.PluginId);
    }

    [Fact]
    public async Task CheckAsync_WithNeitherManifestNorLoadedPlugin_ReportsNothing()
    {
        // Nothing on disk says what version this is, so there is nothing to
        // compare against — a broken plugin must not auto-update on a guess.
        var dll = InstallOnDisk("Acme.Plugin.Foo", manifestVersion: null);
        var checker = NewChecker(installedDlls: [dll], feed: [Entry("Acme.Plugin.Foo", "9.9.9")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_SkipsAPluginThatIsNotOnTheFeed()
    {
        var dll = InstallOnDisk("Acme.Plugin.Private", "1.0.0");
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Other", "9.9.9")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_SkipsPluginsMarkedForUninstall()
    {
        var dll = InstallOnDisk("Acme.Plugin.Foo", "0.8.0");
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "0.9.0")],
            pendingUninstalls: ["Acme.Plugin.Foo.dll"]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_ReturnsEmptyAndSkipsTheFeedWhenNothingIsInstalled()
    {
        var feed = Substitute.For<INuGetPluginFeed>();
        var checker = NewChecker(installedDlls: [], feedSubstitute: feed);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
        await feed.DidNotReceiveWithAnyArgs().FetchAsync(default, default, default);
    }

    [Fact]
    public async Task CheckAsync_HandlesMixedOutcomesInOnePass()
    {
        var stale = InstallOnDisk("Acme.Plugin.Stale", "1.0.0");          // → update
        var current = InstallOnDisk("Acme.Plugin.Current", "2.0.0");      // equal
        var ahead = InstallOnDisk("Acme.Plugin.Ahead", "5.0.0");          // feed older
        var broken = InstallOnDisk("Acme.Plugin.Broken", "wat");          // unparseable
        var absent = InstallOnDisk("Acme.Plugin.Absent", "1.0.0");        // not on feed
        var alsoStale = InstallOnDisk("Acme.Plugin.AlsoStale", "0.1.0");  // → update

        var checker = NewChecker(
            installedDlls: [stale, current, ahead, broken, absent, alsoStale],
            feed:
            [
                Entry("Acme.Plugin.Stale", "1.0.0", "1.1.0"),
                Entry("Acme.Plugin.Current", "2.0.0"),
                Entry("Acme.Plugin.Ahead", "4.0.0"),
                Entry("Acme.Plugin.Broken", "9.9.9"),
                Entry("Acme.Plugin.AlsoStale", "0.1.0", "0.2.0"),
            ]);

        var updates = await checker.CheckAsync(CancellationToken.None);

        Assert.Equal(2, updates.Count);
        Assert.Equal(
            new[] { "Acme.Plugin.AlsoStale", "Acme.Plugin.Stale" },
            updates.Select(u => u.PackageId).OrderBy(id => id, StringComparer.Ordinal).ToArray());
        Assert.Equal("1.1.0", updates.Single(u => u.PackageId == "Acme.Plugin.Stale").LatestVersion);
        Assert.Equal("0.2.0", updates.Single(u => u.PackageId == "Acme.Plugin.AlsoStale").LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_UsesTheManifestVersionOverTheLoadedOne()
    {
        // The manifest describes what was actually written to disk; the loaded
        // instance can be a stale assembly the host hasn't reloaded yet.
        var dll = InstallOnDisk("Acme.Plugin.Foo", "1.1.0");
        var checker = NewChecker(
            installedDlls: [dll],
            feed: [Entry("Acme.Plugin.Foo", "1.1.0")],
            loaded: [Loaded(dll, "com.acme.foo", "1.0.0")]);

        Assert.Empty(await checker.CheckAsync(CancellationToken.None));
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    // Creates plugins/<packageId>/<packageId>.dll, plus a manifest unless
    // manifestVersion is null.
    private string InstallOnDisk(string packageId, string? manifestVersion)
    {
        var dir = Path.Combine(_pluginsDir, packageId);
        Directory.CreateDirectory(dir);
        var dll = Path.Combine(dir, packageId + ".dll");
        File.WriteAllBytes(dll, new byte[] { 0x4D, 0x5A });

        if (manifestVersion is not null)
        {
            _manifests.Write(new InstalledPluginManifest(packageId, manifestVersion, DateTimeOffset.UtcNow));
        }

        return dll;
    }

    private static LoadedPlugin Loaded(string dllPath, string pluginId, string version)
    {
        var plugin = Substitute.For<ITrayPlugin>();
        plugin.Id.Returns(pluginId);
        plugin.Version.Returns(version);
        plugin.DisplayName.Returns(pluginId);
        return new LoadedPlugin(plugin, dllPath, SignatureVerdict.NotSigned);
    }

    private static NuGetPluginEntry Entry(string packageId, params string[] versions)
        => new(
            Id: packageId,
            DisplayName: packageId == "Acme.Plugin.Foo" ? "Foo Plugin" : packageId,
            Publisher: "Acme",
            PublisherUrl: null,
            Description: null,
            Tags: ["proxylayer.azuretray-plugin"],
            SourceRepo: null,
            IconUrl: null,
            NuGetPackageId: packageId,
            Versions: versions
                .Select(v => new NuGetPluginVersion(
                    v, null, null, $"https://nuget/{packageId}/{v}.nupkg", null))
                .ToArray());

    private PluginUpdateChecker NewChecker(
        IReadOnlyList<string> installedDlls,
        IReadOnlyList<NuGetPluginEntry>? feed = null,
        INuGetPluginFeed? feedSubstitute = null,
        IReadOnlyList<LoadedPlugin>? loaded = null,
        IReadOnlyList<string>? pendingUninstalls = null,
        bool includePrerelease = false)
    {
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(installedDlls);
        installer.ListPendingUninstalls().Returns(pendingUninstalls ?? Array.Empty<string>());

        if (feedSubstitute is null)
        {
            feedSubstitute = Substitute.For<INuGetPluginFeed>();
            feedSubstitute
                .FetchAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
                .Returns(feed ?? Array.Empty<NuGetPluginEntry>());
        }

        var loader = Substitute.For<IPluginLoader>();
        loader.LoadedPlugins.Returns(loaded ?? Array.Empty<LoadedPlugin>());

        var preferences = new PluginUpdatePreferenceStore(
            _paths,
            Options.Create(new NuGetPluginFeedOptions { IncludePrereleaseByDefault = includePrerelease }),
            Options.Create(new PluginOptions()),
            NullLogger<PluginUpdatePreferenceStore>.Instance);

        return new PluginUpdateChecker(
            installer,
            _manifests,
            feedSubstitute,
            loader,
            preferences,
            NullLogger<PluginUpdateChecker>.Instance);
    }
}
