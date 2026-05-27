using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using AzureTray;
using AzureTray.AppRegistration;
using AzureTray.Auth;
using AzureTray.Configuration;
using AzureTray.Dto;
using AzureTray.Extensions;
using AzureTray.Graph;
using AzureTray.Plugin.Contracts;
using AzureTray.Models;
using AzureTray.Plugins;
using AzureTray.Shell;
using AzureTray.Tenants;
using AzureTray.ViewModels;
using Xunit;

namespace AzureTray.Tests.ViewModels;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void Constructor_SetsVersionDisplayFromUpdateService()
    {
        var updateService = Substitute.For<IUpdateService>();
        updateService.CurrentVersionDisplay.Returns("1.2.3");

        var vm = NewVm(updateService);

        Assert.Equal("Version 1.2.3", vm.VersionDisplay);
    }

    [Fact]
    public void Constructor_PopulatesTenantsFromStore()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(new[]
        {
            new Tenant("tenant-1", "Contoso", null),
            new Tenant("tenant-2", "Fabrikam", "client-2"),
        });

        var vm = NewVm(tenantStore: store);

        Assert.Equal(2, vm.Tenants.Count);
        Assert.Contains(vm.Tenants, t => t.TenantId == "tenant-1");
        Assert.Contains(vm.Tenants, t => t.TenantId == "tenant-2");
    }

    [Fact]
    public async Task CheckUpdatesCommand_TogglesIsBusyAndSurfacesResult()
    {
        var tcs = new TaskCompletionSource<string>();
        var updateService = Substitute.For<IUpdateService>();
        updateService.CurrentVersionDisplay.Returns("dev");
        updateService.CheckAndApplyAsync().Returns(tcs.Task);

        var vm = NewVm(updateService);
        var execution = vm.CheckUpdatesCommand.ExecuteAsync(null);

        Assert.True(vm.IsBusy);
        Assert.False(vm.CheckUpdatesCommand.CanExecute(null));
        Assert.Equal("Checking…", vm.UpdateStatus);

        tcs.SetResult("Up to date.");
        await execution;

        Assert.False(vm.IsBusy);
        Assert.True(vm.CheckUpdatesCommand.CanExecute(null));
        Assert.Equal("Up to date.", vm.UpdateStatus);
    }

    [Fact]
    public void AddTenantCommand_CannotExecute_WhenTenantIdIsBlank()
    {
        var vm = NewVm();

        Assert.False(vm.AddTenantCommand.CanExecute(null));

        vm.NewTenantId = "   ";
        Assert.False(vm.AddTenantCommand.CanExecute(null));

        vm.NewTenantId = "tenant-1";
        Assert.True(vm.AddTenantCommand.CanExecute(null));
    }

    [Fact]
    public async Task AddTenantCommand_PersistsAndAppendsOnSuccess()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(Array.Empty<Tenant>());

        var graph = Substitute.For<IGraphMeClient>();
        graph.GetMeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MeResponse("id-1", "Alice Example", "alice@contoso.onmicrosoft.com"));

        var credentialFactory = Substitute.For<ICredentialFactory>();

        var vm = NewVm(tenantStore: store, graphMeClient: graph, credentialFactory: credentialFactory);
        vm.NewTenantId = "tenant-1";
        vm.NewTenantDisplayName = "Contoso";
        vm.NewTenantClientId = "11111111-1111-1111-1111-111111111111";

        await vm.AddTenantCommand.ExecuteAsync(null);

        await store.Received().AddOrUpdateAsync(
            Arg.Is<Tenant>(t => t.TenantId == "tenant-1" && t.DisplayName == "Contoso" && t.ClientId == "11111111-1111-1111-1111-111111111111"),
            Arg.Any<CancellationToken>());
        Assert.Single(vm.Tenants);
        Assert.Equal("Contoso", vm.Tenants[0].DisplayName);
        Assert.Equal(string.Empty, vm.NewTenantId);
        Assert.Contains("Alice Example", vm.AddTenantStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddTenantCommand_RollsBackStoreOnSignInFailure()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(Array.Empty<Tenant>());

        var graph = Substitute.For<IGraphMeClient>();
        graph.GetMeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<MeResponse>>(_ => throw new InvalidOperationException("nope"));

        var credentialFactory = Substitute.For<ICredentialFactory>();

        var vm = NewVm(tenantStore: store, graphMeClient: graph, credentialFactory: credentialFactory);
        vm.NewTenantId = "tenant-1";

        await vm.AddTenantCommand.ExecuteAsync(null);

        await store.Received().AddOrUpdateAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
        await store.Received().RemoveAsync("tenant-1", Arg.Any<CancellationToken>());
        credentialFactory.Received(2).Invalidate("tenant-1");
        Assert.Empty(vm.Tenants);
        Assert.StartsWith("Sign-in failed:", vm.AddTenantStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddTenantCommand_RejectsNonGuidClientId_WithoutCallingStoreOrGraph()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(Array.Empty<Tenant>());
        var graph = Substitute.For<IGraphMeClient>();

        var vm = NewVm(tenantStore: store, graphMeClient: graph);
        vm.NewTenantId = "tenant-1";
        vm.NewTenantClientId = "not-a-guid";

        await vm.AddTenantCommand.ExecuteAsync(null);

        Assert.Contains("Client ID must be a GUID", vm.AddTenantStatus, StringComparison.Ordinal);
        await store.DidNotReceive().AddOrUpdateAsync(Arg.Any<Tenant>(), Arg.Any<CancellationToken>());
        await graph.DidNotReceive().GetMeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Empty(vm.Tenants);
    }

    [Fact]
    public async Task AddTenantCommand_AcceptsBlankClientId_AsNull()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(Array.Empty<Tenant>());

        var graph = Substitute.For<IGraphMeClient>();
        graph.GetMeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new MeResponse("id-1", "Alice", "alice@example.com"));

        var vm = NewVm(tenantStore: store, graphMeClient: graph);
        vm.NewTenantId = "tenant-1";
        vm.NewTenantClientId = "   ";

        await vm.AddTenantCommand.ExecuteAsync(null);

        await store.Received().AddOrUpdateAsync(
            Arg.Is<Tenant>(t => t.ClientId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelAddTenantCommand_DuringSignIn_RollsBackAndReleasesUi()
    {
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(Array.Empty<Tenant>());

        var hangingSource = new TaskCompletionSource<MeResponse>();
        var graph = Substitute.For<IGraphMeClient>();
        graph.GetMeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ct = call.Arg<CancellationToken>();
                return hangingSource.Task.WaitAsync(ct);
            });

        var credentialFactory = Substitute.For<ICredentialFactory>();
        var vm = NewVm(tenantStore: store, graphMeClient: graph, credentialFactory: credentialFactory);
        vm.NewTenantId = "tenant-1";

        var addTask = vm.AddTenantCommand.ExecuteAsync(null);

        // Wait until the watcher has set IsAddingTenant = true.
        var sw = Stopwatch.StartNew();
        while (!vm.IsAddingTenant && sw.Elapsed < TimeSpan.FromSeconds(2))
        {
            await Task.Delay(10);
        }
        Assert.True(vm.IsAddingTenant);
        Assert.True(vm.CancelAddTenantCommand.CanExecute(null));

        vm.CancelAddTenantCommand.Execute(null);

        await addTask;

        Assert.False(vm.IsAddingTenant);
        Assert.False(vm.CancelAddTenantCommand.CanExecute(null));
        Assert.True(vm.AddTenantCommand.CanExecute(null));
        Assert.Contains("cancel", vm.AddTenantStatus, StringComparison.OrdinalIgnoreCase);
        await store.Received().RemoveAsync("tenant-1", Arg.Any<CancellationToken>());
        credentialFactory.Received().Invalidate("tenant-1");
    }

    [Fact]
    public async Task AvailablePluginsExpander_AutoFetches_AndHidesAlreadyInstalled()
    {
        var fetched = new[]
        {
            new NuGetPluginEntry(
                Id: "Acme.Plugin.Foo",
                DisplayName: "Foo Plugin",
                Publisher: "Acme",
                PublisherUrl: null,
                Description: "Does Foo things.",
                Tags: new[] { "proxylayer.azuretray-plugin" },
                SourceRepo: null,
                IconUrl: null,
                NuGetPackageId: "Acme.Plugin.Foo",
                Versions: new[] { new NuGetPluginVersion("1.0.0", null, null, "https://nuget/foo.nupkg", null) }),
            new NuGetPluginEntry(
                Id: "Acme.Plugin.Bar",
                DisplayName: "Bar Plugin",
                Publisher: "Acme",
                PublisherUrl: null,
                Description: "Does Bar things.",
                Tags: new[] { "proxylayer.azuretray-plugin" },
                SourceRepo: null,
                IconUrl: null,
                NuGetPackageId: "Acme.Plugin.Bar",
                Versions: new[] { new NuGetPluginVersion("2.0.0", null, null, "https://nuget/bar.nupkg", null) }),
        };
        var feed = Substitute.For<INuGetPluginFeed>();
        feed.FetchAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>())
            .Returns((IReadOnlyList<NuGetPluginEntry>)fetched);

        // Pre-installed: Foo. Should drop out of the available list.
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(new[] { @"C:\plugins\Acme.Plugin.Foo\Acme.Plugin.Foo.dll" });
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());

        var vm = NewVm(extensionInstaller: installer, nuGetFeed: feed);

        Assert.Empty(vm.AvailableOnlinePlugins);
        Assert.False(vm.IsAvailablePluginsExpanded);

        vm.IsAvailablePluginsExpanded = true;

        // OnIsAvailablePluginsExpandedChanged starts an awaited task we
        // can't access; poll a few times until it lands.
        for (var i = 0; i < 50 && vm.AvailableOnlinePlugins.Count == 0; i++)
        {
            await Task.Yield();
            await Task.Delay(10);
        }

        await feed.Received().FetchAsync(Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>(), Arg.Any<bool>());
        Assert.Single(vm.AvailableOnlinePlugins);
        Assert.Equal("Acme.Plugin.Bar", vm.AvailableOnlinePlugins[0].Id);

        // Filter narrows the visible list. "bar" matches Bar.
        vm.OnlinePluginFilter = "bar";
        Assert.Single(vm.AvailableOnlinePlugins);

        // Filter that matches nothing surfaces the "no match" empty-state.
        vm.OnlinePluginFilter = "nonexistent";
        Assert.Empty(vm.AvailableOnlinePlugins);
        Assert.True(vm.HasAvailableEmptyMessage);
        Assert.Contains("nonexistent", vm.AvailableEmptyMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallExtensionCommand_UnsignedAccepted_LoadsAndKeepsRow()
    {
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(Array.Empty<string>(), new[] { @"C:\plugins\MyPlugin\MyPlugin.dll" });
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());
        installer.InstallFromFileAsync(@"C:\src\MyPlugin.nupkg", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { @"C:\plugins\MyPlugin\MyPlugin.dll" });

        var fileDialog = Substitute.For<IFileDialogService>();
        fileDialog.OpenFile(Arg.Any<string>(), Arg.Any<string>()).Returns(@"C:\src\MyPlugin.nupkg");

        var verifier = Substitute.For<IPluginSignatureVerifier>();
        verifier.Verify(Arg.Any<string>()).Returns(SignatureVerdict.NotSigned);

        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns((NotificationResult)new YesNoResult(Accepted: true));

        var vm = NewVm(
            extensionInstaller: installer,
            fileDialogService: fileDialog,
            signatureVerifier: verifier,
            notifier: notifier);

        await vm.InstallExtensionCommand.ExecuteAsync(null);

        await notifier.Received().ShowAsync(Arg.Any<YesNoRequest>(), Arg.Any<CancellationToken>());
        await installer.DidNotReceiveWithAnyArgs().TryDeleteAsync(default!, default);
        Assert.Single(vm.InstalledExtensions);
    }

    [Fact]
    public async Task InstallExtensionCommand_UnsignedDeclined_RollsBack()
    {
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(Array.Empty<string>(), Array.Empty<string>());
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());
        installer.InstallFromFileAsync(@"C:\src\MyPlugin.nupkg", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { @"C:\plugins\MyPlugin\MyPlugin.dll" });
        installer.TryDeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);

        var fileDialog = Substitute.For<IFileDialogService>();
        fileDialog.OpenFile(Arg.Any<string>(), Arg.Any<string>()).Returns(@"C:\src\MyPlugin.nupkg");

        var verifier = Substitute.For<IPluginSignatureVerifier>();
        verifier.Verify(Arg.Any<string>()).Returns(SignatureVerdict.NotSigned);

        var notifier = Substitute.For<INotifier>();
        notifier.ShowAsync(Arg.Any<NotificationRequest>(), Arg.Any<CancellationToken>())
            .Returns((NotificationResult)new YesNoResult(Accepted: false));

        var loader = Substitute.For<IPluginLoader>();
        loader.LoadedPlugins.Returns(Array.Empty<LoadedPlugin>());

        var vm = NewVm(
            extensionInstaller: installer,
            fileDialogService: fileDialog,
            pluginLoader: loader,
            signatureVerifier: verifier,
            notifier: notifier);

        await vm.InstallExtensionCommand.ExecuteAsync(null);

        await notifier.Received().ShowAsync(Arg.Any<YesNoRequest>(), Arg.Any<CancellationToken>());
        await installer.Received().TryDeleteAsync(@"C:\plugins\MyPlugin\MyPlugin.dll", Arg.Any<CancellationToken>());
        await loader.DidNotReceiveWithAnyArgs().LoadOneAsync(default!, default);
        Assert.Empty(vm.InstalledExtensions);
    }

    [Fact]
    public async Task InstallExtensionCommand_SignedPlugin_SkipsPrompt()
    {
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(Array.Empty<string>(), new[] { @"C:\plugins\MyPlugin\MyPlugin.dll" });
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());
        installer.InstallFromFileAsync(@"C:\src\MyPlugin.nupkg", Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<string>)new[] { @"C:\plugins\MyPlugin\MyPlugin.dll" });

        var fileDialog = Substitute.For<IFileDialogService>();
        fileDialog.OpenFile(Arg.Any<string>(), Arg.Any<string>()).Returns(@"C:\src\MyPlugin.nupkg");

        var verifier = Substitute.For<IPluginSignatureVerifier>();
        verifier.Verify(Arg.Any<string>()).Returns(new SignatureVerdict(IsSigned: true, SignerThumbprint: "DEAD", Subject: "CN=Test"));

        var notifier = Substitute.For<INotifier>();

        var vm = NewVm(
            extensionInstaller: installer,
            fileDialogService: fileDialog,
            signatureVerifier: verifier,
            notifier: notifier);

        await vm.InstallExtensionCommand.ExecuteAsync(null);

        await notifier.DidNotReceiveWithAnyArgs().ShowAsync(default!, default);
        Assert.Single(vm.InstalledExtensions);
    }

    [Fact]
    public async Task InstallExtensionCommand_DoesNothing_WhenDialogCancelled()
    {
        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls().Returns(Array.Empty<string>());
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());

        var fileDialog = Substitute.For<IFileDialogService>();
        fileDialog.OpenFile(Arg.Any<string>(), Arg.Any<string>()).Returns((string?)null);

        var vm = NewVm(extensionInstaller: installer, fileDialogService: fileDialog);

        await vm.InstallExtensionCommand.ExecuteAsync(null);

        await installer.DidNotReceiveWithAnyArgs().InstallFromFileAsync(default!, default);
        Assert.Equal(string.Empty, vm.ExtensionStatus);
    }

    [Fact]
    public async Task UninstallExtensionCommand_HotDeletes_AndRefreshes()
    {
        var existing = new InstalledExtension(
            "MyPlugin.dll", @"C:\plugins\MyPlugin.dll",
            IsPendingUninstall: false, IsLoaded: false, PluginId: null, LoadedDisplayName: null, LoadedVersion: null);

        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls()
            .Returns(new[] { @"C:\plugins\MyPlugin.dll" }, Array.Empty<string>());
        installer.ListPendingUninstalls().Returns(Array.Empty<string>());
        installer.TryDeleteAsync(@"C:\plugins\MyPlugin.dll", Arg.Any<CancellationToken>()).Returns(true);

        var vm = NewVm(extensionInstaller: installer);

        await vm.UninstallExtensionCommand.ExecuteAsync(existing);

        await installer.Received().TryDeleteAsync(@"C:\plugins\MyPlugin.dll", Arg.Any<CancellationToken>());
        await installer.DidNotReceiveWithAnyArgs().RequestUninstallAsync(default!, default);
        Assert.Contains("Uninstalled MyPlugin.dll", vm.ExtensionStatus, StringComparison.Ordinal);
        Assert.Empty(vm.InstalledExtensions);
    }

    [Fact]
    public async Task UninstallExtensionCommand_FallsBackToSentinel_WhenHotDeleteBlocked()
    {
        var existing = new InstalledExtension(
            "MyPlugin.dll", @"C:\plugins\MyPlugin.dll",
            IsPendingUninstall: false, IsLoaded: false, PluginId: null, LoadedDisplayName: null, LoadedVersion: null);

        var installer = Substitute.For<IExtensionInstaller>();
        installer.ListInstalledDlls()
            .Returns(new[] { @"C:\plugins\MyPlugin.dll" }, new[] { @"C:\plugins\MyPlugin.dll" });
        installer.ListPendingUninstalls()
            .Returns(Array.Empty<string>(), new[] { "MyPlugin.dll" });
        installer.TryDeleteAsync(@"C:\plugins\MyPlugin.dll", Arg.Any<CancellationToken>()).Returns(false);

        var vm = NewVm(extensionInstaller: installer);

        await vm.UninstallExtensionCommand.ExecuteAsync(existing);

        await installer.Received().TryDeleteAsync(@"C:\plugins\MyPlugin.dll", Arg.Any<CancellationToken>());
        await installer.Received().RequestUninstallAsync(@"C:\plugins\MyPlugin.dll", Arg.Any<CancellationToken>());
        Assert.Contains("Uninstalled MyPlugin.dll", vm.ExtensionStatus, StringComparison.Ordinal);
        // Row must disappear immediately even when the sentinel-fallback
        // path was used — files are pending cleanup but the user-facing
        // list shouldn't carry the dead plugin anymore.
        Assert.Empty(vm.InstalledExtensions);
    }

    [Fact]
    public async Task RemoveTenantCommand_RemovesFromStoreAndCollection()
    {
        var existing = new Tenant("tenant-1", "Contoso", null);
        var store = Substitute.For<ITenantStore>();
        store.GetAll().Returns(new[] { existing });

        var credentialFactory = Substitute.For<ICredentialFactory>();

        var vm = NewVm(tenantStore: store, credentialFactory: credentialFactory);
        Assert.Single(vm.Tenants);

        await vm.RemoveTenantCommand.ExecuteAsync(existing);

        await store.Received().RemoveAsync("tenant-1", Arg.Any<CancellationToken>());
        credentialFactory.Received().Invalidate("tenant-1");
        Assert.Empty(vm.Tenants);
    }

    private static SettingsViewModel NewVm(
        IUpdateService? updateService = null,
        IGraphMeClient? graphMeClient = null,
        ITenantStore? tenantStore = null,
        ICredentialFactory? credentialFactory = null,
        IExtensionInstaller? extensionInstaller = null,
        IFileDialogService? fileDialogService = null,
        IPluginLoader? pluginLoader = null,
        IPluginSignatureVerifier? signatureVerifier = null,
        INotifier? notifier = null,
        INuGetPluginFeed? nuGetFeed = null,
        IOpenIdConfigClient? oidc = null,
        IAppRegistrationDiscovery? appRegistrationDiscovery = null,
        IAppRegistrationPermissions? appRegistrationPermissions = null,
        IAppRegistrationProvisioning? appRegistrationProvisioning = null,
        ITenantReadinessTracker? readiness = null,
        AuthOptions? authOptions = null,
        PluginOptions? pluginOptions = null)
    {
        updateService ??= Substitute.For<IUpdateService>();
        graphMeClient ??= Substitute.For<IGraphMeClient>();
        tenantStore ??= Substitute.For<ITenantStore>();
        if (tenantStore.GetAll() is null)
        {
            tenantStore.GetAll().Returns(Array.Empty<Tenant>());
        }
        credentialFactory ??= Substitute.For<ICredentialFactory>();

        if (extensionInstaller is null)
        {
            extensionInstaller = Substitute.For<IExtensionInstaller>();
            extensionInstaller.ListInstalledDlls().Returns(Array.Empty<string>());
            extensionInstaller.ListPendingUninstalls().Returns(Array.Empty<string>());
        }
        fileDialogService ??= Substitute.For<IFileDialogService>();
        nuGetFeed ??= Substitute.For<INuGetPluginFeed>();
        var packageSecurityScanner = Substitute.For<IPackageSecurityScanner>();

        if (pluginLoader is null)
        {
            pluginLoader = Substitute.For<IPluginLoader>();
            pluginLoader.LoadedPlugins.Returns(Array.Empty<LoadedPlugin>());
        }

        oidc ??= Substitute.For<IOpenIdConfigClient>();
        appRegistrationDiscovery ??= Substitute.For<IAppRegistrationDiscovery>();
        appRegistrationPermissions ??= Substitute.For<IAppRegistrationPermissions>();
        appRegistrationProvisioning ??= Substitute.For<IAppRegistrationProvisioning>();
        notifier ??= Substitute.For<INotifier>();
        readiness ??= new TenantReadinessTracker();
        var authHealth = Substitute.For<ITenantAuthHealth>();
        var windowsSignIn = Substitute.For<IWindowsAccountSignInService>();
        var organizationInfo = Substitute.For<IGraphOrganizationClient>();
        var startupManager = Substitute.For<IStartupManager>();
        authOptions ??= new AuthOptions();
        pluginOptions ??= new PluginOptions();
        if (signatureVerifier is null)
        {
            signatureVerifier = Substitute.For<IPluginSignatureVerifier>();
            // Default: report unsigned. Tests that want signed assert it explicitly.
            signatureVerifier.Verify(Arg.Any<string>()).Returns(SignatureVerdict.NotSigned);
        }
        var pluginConfigStore = Substitute.For<IPluginConfigStore>();
        pluginConfigStore.IsTenantEnabledFor(Arg.Any<string>(), Arg.Any<string>()).Returns(true);
        pluginConfigStore.GetDisabledTenants(Arg.Any<string>()).Returns(new HashSet<string>());
        pluginConfigStore.GetOptions(Arg.Any<string>()).Returns(new Dictionary<string, object?>());

        return new SettingsViewModel(
            updateService,
            graphMeClient,
            tenantStore,
            credentialFactory,
            extensionInstaller,
            nuGetFeed,
            packageSecurityScanner,
            fileDialogService,
            pluginLoader,
            pluginConfigStore,
            signatureVerifier,
            oidc,
            appRegistrationDiscovery,
            appRegistrationPermissions,
            appRegistrationProvisioning,
            notifier,
            readiness,
            authHealth,
            windowsSignIn,
            organizationInfo,
            startupManager,
            Options.Create(authOptions),
            Options.Create(pluginOptions),
            NullLogger<SettingsViewModel>.Instance);
    }
}
