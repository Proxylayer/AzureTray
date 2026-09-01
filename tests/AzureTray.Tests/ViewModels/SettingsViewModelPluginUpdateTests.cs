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
using AzureTray.AppRegistration;
using AzureTray.Auth;
using AzureTray.AzureCloud;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Graph;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Shell;
using AzureTray.Tenants;
using AzureTray.Tests.Extensions;
using AzureTray.ViewModels;
using Xunit;

namespace AzureTray.Tests.ViewModels;

// Regression pin for the in-place update path.
//
// The pre-existing rollback for a declined install deletes what it just wrote,
// which for a subfolder plugin means deleting the whole plugins/<id>/ folder.
// That is correct for a FRESH install and destructive for an UPDATE: saying
// "no" to the unsigned-plugin prompt on a version bump would uninstall a
// plugin the user was happily running. Both halves are pinned here.
[Collection(PluginBackupTemp.Name)]
public sealed class SettingsViewModelPluginUpdateTests : IDisposable
{
    private const string PackageId = "Acme.Plugin.Foo";
    private const string OriginalBytes = "installed-v1";
    private const string DownloadedBytes = "downloaded-v2";

    private readonly string _root;
    private readonly string _pluginsDir;
    private readonly string _configDir;
    private readonly string _installedDll;
    private readonly IAppPaths _paths;

    public SettingsViewModelPluginUpdateTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "AzureTray.Tests.VmPluginUpdate", Guid.NewGuid().ToString("N"));
        _pluginsDir = Path.Combine(_root, "plugins");
        _configDir = Path.Combine(_root, "config");
        Directory.CreateDirectory(Path.Combine(_pluginsDir, PackageId));
        Directory.CreateDirectory(_configDir);

        _installedDll = Path.Combine(_pluginsDir, PackageId, PackageId + ".dll");
        File.WriteAllText(_installedDll, OriginalBytes);
        File.WriteAllText(Path.Combine(_pluginsDir, PackageId, "Dep.dll"), "dep-v1");

        _paths = Substitute.For<IAppPaths>();
        _paths.PluginsDir.Returns(_pluginsDir);
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
    public async Task UpdateExtensionCommand_DeclinedUnsignedUpdate_LeavesTheInstalledPluginIntact()
    {
        var installer = NewInstaller();
        installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                // The installer really does overwrite the live folder.
                File.WriteAllText(_installedDll, DownloadedBytes);
                File.Delete(Path.Combine(_pluginsDir, PackageId, "Dep.dll"));
                return Task.FromResult<IReadOnlyList<string>>(new[] { _installedDll });
            });

        var notifier = DecliningNotifier();
        var state = new PluginUpdateState();
        var vm = NewVm(installer, notifier, state);

        state.Publish([Update()]);
        await vm.UpdateExtensionCommand.ExecuteAsync(Row());

        // The plugin the user already had must still be there, byte for byte.
        Assert.True(File.Exists(_installedDll));
        Assert.Equal(OriginalBytes, File.ReadAllText(_installedDll));
        Assert.Equal("dep-v1", File.ReadAllText(Path.Combine(_pluginsDir, PackageId, "Dep.dll")));

        // …and the fresh-install cleanup (which deletes the whole folder) must
        // NOT have run.
        await installer.DidNotReceiveWithAnyArgs().TryDeleteAsync(default!, default);
        Assert.Contains("kept", vm.OnlinePluginsStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateExtensionCommand_FailedUpdate_RestoresThePreviousVersion()
    {
        var installer = NewInstaller();
        installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => FailMidInstall());

        var state = new PluginUpdateState();
        var vm = NewVm(installer, Substitute.For<INotifier>(), state);

        state.Publish([Update()]);
        await vm.UpdateExtensionCommand.ExecuteAsync(Row());

        Assert.Equal(OriginalBytes, File.ReadAllText(_installedDll));
        Assert.Equal("dep-v1", File.ReadAllText(Path.Combine(_pluginsDir, PackageId, "Dep.dll")));
        await installer.DidNotReceiveWithAnyArgs().TryDeleteAsync(default!, default);

        Task<IReadOnlyList<string>> FailMidInstall()
        {
            File.WriteAllText(_installedDll, "half-written");
            throw new IOException("connection reset");
        }
    }

    [Fact]
    public async Task UpdateExtensionCommand_AcceptedSignedUpdate_KeepsTheNewBytesAndClearsTheBanner()
    {
        var installer = NewInstaller();
        installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                File.WriteAllText(_installedDll, DownloadedBytes);
                return Task.FromResult<IReadOnlyList<string>>(new[] { _installedDll });
            });

        var verifier = Substitute.For<IPluginSignatureVerifier>();
        verifier.Verify(Arg.Any<string>())
            .Returns(new SignatureVerdict(IsSigned: true, SignerThumbprint: "DEAD", Subject: "CN=Acme"));

        var state = new PluginUpdateState();
        var vm = NewVm(installer, Substitute.For<INotifier>(), state, verifier);

        state.Publish([Update()]);
        Assert.True(vm.IsPluginUpdateAvailable);

        await vm.UpdateExtensionCommand.ExecuteAsync(Row());

        Assert.Equal(DownloadedBytes, File.ReadAllText(_installedDll));
        await installer.DidNotReceiveWithAnyArgs().TryDeleteAsync(default!, default);
        // The row's button and the banner clear without waiting for a poll.
        Assert.Empty(state.Available);
        Assert.False(vm.IsPluginUpdateAvailable);
    }

    [Fact]
    public async Task UpdateExtensionCommand_WithNoPendingUpdate_DoesNothing()
    {
        var installer = NewInstaller();
        var vm = NewVm(installer, Substitute.For<INotifier>(), new PluginUpdateState());

        await vm.UpdateExtensionCommand.ExecuteAsync(Row());

        await installer.DidNotReceiveWithAnyArgs().InstallFromUrlAsync(default!, default!, default, default);
        Assert.Contains("No update is pending", vm.ExtensionStatus, StringComparison.Ordinal);
    }

    // The other half of the pin: a declined FRESH install must still clean up
    // everything it wrote.
    [Fact]
    public async Task InstallOnlinePluginCommand_DeclinedUnsignedInstall_CleansUpEverythingItWrote()
    {
        var newDir = Path.Combine(_pluginsDir, "Acme.Plugin.New");
        Directory.CreateDirectory(newDir);
        var newDll = Path.Combine(newDir, "Acme.Plugin.New.dll");
        var newDep = Path.Combine(newDir, "Dep.dll");
        File.WriteAllText(newDll, DownloadedBytes);
        File.WriteAllText(newDep, "dep");

        var installer = NewInstaller();
        installer.InstallFromUrlAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { newDll, newDep }));
        installer.TryDeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var vm = NewVm(installer, DecliningNotifier(), new PluginUpdateState());

        await vm.InstallOnlinePluginCommand.ExecuteAsync(Entry("Acme.Plugin.New", "1.0.0"));

        await installer.Received().TryDeleteAsync(newDll, Arg.Any<CancellationToken>());
        await installer.Received().TryDeleteAsync(newDep, Arg.Any<CancellationToken>());
        Assert.Contains("cancelled", vm.OnlinePluginsStatus, StringComparison.OrdinalIgnoreCase);
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private static INotifier DecliningNotifier()
    {
        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns((NotificationResult)new YesNoResult(Accepted: false));
        return notifier;
    }

    private IExtensionInstaller NewInstaller()
    {
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(_ => new[] { _installedDll });
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());
        return installer;
    }

    private InstalledExtension Row()
        => new(
            FileName: PackageId + ".dll",
            FullPath: _installedDll,
            IsPendingUninstall: false,
            IsLoaded: true,
            PluginId: "com.acme.foo",
            LoadedDisplayName: "Foo Plugin",
            LoadedVersion: "1.0.0",
            PackageId: PackageId,
            InstalledVersion: "1.0.0",
            AvailableUpdateVersion: "2.0.0");

    // NuGetPackageId is deliberately null so the GHSA scan is skipped: these
    // tests are about the rollback, and the scanner has its own coverage.
    private static NuGetPluginEntry Entry(string packageId, string version)
        => new(
            Id: packageId,
            DisplayName: "Foo Plugin",
            Publisher: "Acme",
            PublisherUrl: null,
            Description: null,
            Tags: ["proxylayer.azuretray-plugin"],
            SourceRepo: null,
            IconUrl: null,
            NuGetPackageId: null,
            Versions: [new NuGetPluginVersion(version, null, null, $"https://nuget/{packageId}/{version}.nupkg", null)]);

    private PluginUpdate Update()
    {
        var entry = Entry(PackageId, "2.0.0");
        return new PluginUpdate(
            PackageId: PackageId,
            PluginId: "com.acme.foo",
            InstalledVersion: "1.0.0",
            InstalledDllPath: _installedDll,
            Entry: entry,
            Latest: entry.Versions[0]);
    }

    private SettingsViewModel NewVm(
        IExtensionInstaller extensionInstaller,
        INotifier notifier,
        PluginUpdateState pluginUpdateState,
        IPluginSignatureVerifier? signatureVerifier = null)
    {
        var updateService = Substitute.For<IUpdateService>();
        var tenantStore = Substitute.For<ITenantStore>();
        tenantStore.GetAll().Returns(Array.Empty<Tenant>());

        var pluginLoader = Substitute.For<IPluginLoader>();
        pluginLoader.LoadedPlugins.Returns(Array.Empty<LoadedPlugin>());

        if (signatureVerifier is null)
        {
            signatureVerifier = Substitute.For<IPluginSignatureVerifier>();
            signatureVerifier.Verify(Arg.Any<string>()).Returns(SignatureVerdict.NotSigned);
        }

        var pluginOptions = new PluginOptions();
        var pluginConfigStore = Substitute.For<IPluginConfigStore>();
        pluginConfigStore.IsTenantEnabledFor(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        pluginConfigStore.GetDisabledTenants(Arg.Any<string>()).Returns(new HashSet<string>());
        pluginConfigStore.GetOptions(Arg.Any<string>()).Returns(new Dictionary<string, object?>());

        return new SettingsViewModel(
            updateService,
            Substitute.For<IGraphMeClient>(),
            tenantStore,
            Substitute.For<ICredentialFactory>(),
            Substitute.For<IAzureCloudConfig>(),
            extensionInstaller,
            Substitute.For<INuGetPluginFeed>(),
            Substitute.For<IPackageSecurityScanner>(),
            new PluginManifestStore(_paths, NullLogger<PluginManifestStore>.Instance),
            Substitute.For<IPluginUpdateChecker>(),
            pluginUpdateState,
            new PluginUpdatePreferenceStore(
                _paths,
                Options.Create(new NuGetPluginFeedOptions()),
                Options.Create(pluginOptions),
                NullLogger<PluginUpdatePreferenceStore>.Instance),
            Substitute.For<IFileDialogService>(),
            pluginLoader,
            pluginConfigStore,
            signatureVerifier,
            Substitute.For<IOpenIdConfigClient>(),
            Substitute.For<IAppRegistrationDiscovery>(),
            Substitute.For<IAppRegistrationPermissions>(),
            Substitute.For<IAppRegistrationProvisioning>(),
            notifier,
            new TenantReadinessTracker(),
            Substitute.For<ITenantAuthHealth>(),
            Substitute.For<IWindowsAccountSignInService>(),
            Substitute.For<IGraphOrganizationClient>(),
            Substitute.For<IStartupManager>(),
            new TokenFreshnessGate(),
            Options.Create(new AuthOptions()),
            Options.Create(pluginOptions),
            NullLogger<SettingsViewModel>.Instance);
    }
}
