using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Notifications;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using Xunit;

namespace AzureTray.Tests.Extensions;

// The poll loop owns the auto-update on/off decision: the auto-updater itself
// only knows how to refuse unsafe updates, not whether it should have been
// asked at all.
//
// PollOnceAsync is reached by reflection because the only public entry point
// (ExecuteAsync, via StartAsync) sleeps a full configured interval — a minimum
// of one hour — before its first tick, which no headless test can wait for.
// Renaming the method breaks these tests loudly rather than silently.
[Collection(PluginBackupTemp.Name)]
public sealed class PluginUpdatePollingServiceTests : IDisposable
{
    private const string PackageId = "Acme.Plugin.Foo";
    private const string InstalledBytes = "installed-v1";
    private const string UpdatedBytes = "downloaded-v2";

    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly string _configDir;
    private readonly string _installedDll;
    private readonly IAppPaths _paths;

    private readonly IExtensionInstaller _installer = Substitute.For<IExtensionInstaller>();
    private readonly IPackageSecurityScanner _scanner = Substitute.For<IPackageSecurityScanner>();
    private readonly IPluginSignatureVerifier _verifier = Substitute.For<IPluginSignatureVerifier>();
    private readonly IPluginLoader _loader = Substitute.For<IPluginLoader>();
    private readonly IPluginUpdateChecker _checker = Substitute.For<IPluginUpdateChecker>();

    public PluginUpdatePollingServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.PluginPoll", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        _configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(_pluginsDir, PackageId));
        Directory.CreateDirectory(_configDir);
        _installedDll = Path.Combine(_pluginsDir, PackageId, PackageId + ".dll");
        File.WriteAllText(_installedDll, InstalledBytes);

        _paths = Substitute.For<IAppPaths>();
        _paths.PluginsDir.Returns(_pluginsDir);
        _paths.ConfigDir.Returns(_configDir);

        _scanner.ScanAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PackageSecurityScanResult(
                PackageId, "2.0.0", Array.Empty<SecurityAdvisory>(), ScanSucceeded: true, ScanError: null));
        _verifier.Verify(Arg.Any<string>())
            .Returns(new SignatureVerdict(IsSigned: true, SignerThumbprint: "DEAD", Subject: "CN=Acme"));
        _loader.LoadedPlugins.Returns(Array.Empty<LoadedPlugin>());
        _loader.LoadOrReloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<LoadedPlugin?>(Loaded()));
        _installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                File.WriteAllText(_installedDll, UpdatedBytes);
                return Task.FromResult<IReadOnlyList<string>>(new[] { _installedDll });
            });
        _checker.CheckAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<PluginUpdate>)new[] { Update() });
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
    public async Task PollOnce_WithAutoUpdateOff_PublishesTheUpdateButInstallsNothing()
    {
        var state = new PluginUpdateState();
        var service = NewService(state, autoUpdateEnabled: false);

        await PollOnceAsync(service);

        Assert.Equal(PackageId, Assert.Single(state.Available).PackageId);
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
        Assert.Equal(InstalledBytes, File.ReadAllText(_installedDll));
    }

    [Fact]
    public async Task PollOnce_WithAutoUpdateOn_AppliesTheUpdateAndClearsTheSnapshot()
    {
        var state = new PluginUpdateState();
        var service = NewService(state, autoUpdateEnabled: true);

        await PollOnceAsync(service);

        await _installer.Received().InstallFromUrlAsync(
            PackageId, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.Equal(UpdatedBytes, File.ReadAllText(_installedDll));
        // Applied updates drop out of the snapshot so the banner clears.
        Assert.Empty(state.Available);
    }

    [Fact]
    public async Task PollOnce_RepeatedTicksWithTheSameUpdateChangeNothing()
    {
        var state = new PluginUpdateState();
        var service = NewService(state, autoUpdateEnabled: false);

        await PollOnceAsync(service);
        await PollOnceAsync(service);

        // Two ticks, one update: the snapshot is republished but the update is
        // already known, so nothing new happens.
        Assert.Single(state.Available);
        await _checker.Received(2).CheckAsync(Arg.Any<CancellationToken>());
        await _installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ExecuteAsync_WithTheIntervalDisabled_NeverChecks()
    {
        var service = NewService(new PluginUpdateState(), autoUpdateEnabled: false, intervalHours: 0);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        await _checker.DidNotReceiveWithAnyArgs().CheckAsync(default);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static Task PollOnceAsync(PluginUpdatePollingService service)
    {
        var method = typeof(PluginUpdatePollingService)
            .GetMethod("PollOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(service, new object[] { CancellationToken.None }) as Task;
        Assert.NotNull(task);
        return task!;
    }

    private static LoadedPlugin Loaded()
    {
        var plugin = Substitute.For<ITrayPlugin>();
        plugin.Id.Returns("com.acme.foo");
        plugin.Version.Returns("2.0.0");
        plugin.DisplayName.Returns("Foo Plugin");
        return new LoadedPlugin(plugin, "ignored.dll", SignatureVerdict.NotSigned);
    }

    private PluginUpdate Update()
    {
        var latest = new NuGetPluginVersion("2.0.0", null, null, "https://nuget/foo/2.0.0.nupkg", null);
        var entry = new NuGetPluginEntry(
            Id: PackageId,
            DisplayName: "Foo Plugin",
            Publisher: "Acme",
            PublisherUrl: null,
            Description: null,
            Tags: ["proxylayer.azuretray-plugin"],
            SourceRepo: null,
            IconUrl: null,
            NuGetPackageId: PackageId,
            Versions: [latest]);

        return new PluginUpdate(
            PackageId: PackageId,
            PluginId: "com.acme.foo",
            InstalledVersion: "1.0.0",
            InstalledDllPath: _installedDll,
            Entry: entry,
            Latest: latest);
    }

    private PluginUpdatePollingService NewService(
        PluginUpdateState state,
        bool autoUpdateEnabled,
        int intervalHours = 6)
    {
        var pluginOptions = new PluginOptions { UpdateCheckIntervalHours = intervalHours };
        var preferences = new PluginUpdatePreferenceStore(
            _paths,
            Options.Create(new NuGetPluginFeedOptions()),
            Options.Create(pluginOptions),
            NullLogger<PluginUpdatePreferenceStore>.Instance)
        {
            AutoUpdateEnabled = autoUpdateEnabled,
        };

        var notifier = new PluginUpdateNotifier(
            Substitute.For<INotifier>(), NullLogger<PluginUpdateNotifier>.Instance);

        var autoUpdater = new PluginAutoUpdater(
            _installer,
            _scanner,
            _verifier,
            _loader,
            notifier,
            Options.Create(pluginOptions),
            NullLogger<PluginAutoUpdater>.Instance);

        return new PluginUpdatePollingService(
            _checker,
            state,
            notifier,
            autoUpdater,
            preferences,
            Options.Create(pluginOptions),
            NullLogger<PluginUpdatePollingService>.Instance);
    }
}
