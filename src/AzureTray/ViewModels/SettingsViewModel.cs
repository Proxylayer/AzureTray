using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.AppRegistration;
using AzureTray.Auth;
using AzureTray.Configuration;
using AzureTray.Extensions;
using AzureTray.Graph;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Shell;
using AzureTray.Tenants;

namespace AzureTray.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    // Hard upper bound on the interactive sign-in. Azure.Identity can hang
    // when the ClientId / TenantId combination is wrong; without a timeout
    // the form has no escape hatch other than killing the process.
    private static readonly TimeSpan SignInTimeout = TimeSpan.FromSeconds(90);

    private readonly IUpdateService _updateService;
    private readonly IGraphMeClient _graphMeClient;
    private readonly ITenantStore _tenantStore;
    private readonly ICredentialFactory _credentialFactory;
    private readonly IExtensionInstaller _extensionInstaller;
    private readonly INuGetPluginFeed _nuGetFeed;
    private readonly IPackageSecurityScanner _packageSecurityScanner;
    private readonly IFileDialogService _fileDialogService;
    private readonly IPluginLoader _pluginLoader;
    private readonly IOpenIdConfigClient _oidc;
    private readonly IAppRegistrationDiscovery _appRegistrationDiscovery;
    private readonly IAppRegistrationPermissions _appRegistrationPermissions;
    private readonly IAppRegistrationProvisioning _appRegistrationProvisioning;
    private readonly INotifier _notifier;
    private readonly ITenantReadinessTracker _readiness;
    private readonly IWindowsAccountSignInService _windowsSignIn;
    private readonly IGraphOrganizationClient _organizationInfo;
    private readonly IStartupManager _startupManager;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<SettingsViewModel> _logger;

    // Guard so flipping LaunchAtStartup from the ctor (initial sync of the
    // checkbox to the registry value) doesn't re-enter the registry write.
    private bool _suppressLaunchAtStartupCommit;

    private CancellationTokenSource? _addTenantCts;

    [ObservableProperty]
    private string _versionDisplay = string.Empty;

    [ObservableProperty]
    private string _updateStatus = string.Empty;

    // True when IUpdateService.CheckOnStartupAsync has detected (and
    // downloaded) a newer release. Drives the blue clickable banner at
    // the top of the Settings window. Initial value seeded from
    // _updateService.PendingUpdateVersion in the ctor — covers the case
    // where the update was detected before Settings was first opened.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBannerText))]
    private string? _pendingUpdateVersion;

    public string UpdateBannerText => string.IsNullOrEmpty(PendingUpdateVersion)
        ? string.Empty
        : $"Update available: v{PendingUpdateVersion}. Click here to install and restart.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand))]
    [NotifyCanExecuteChangedFor(nameof(ApplyPendingUpdateCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupDomainCommand))]
    private string _newTenantDomain = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTenantCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private string _newTenantId = string.Empty;

    [ObservableProperty]
    private string _newTenantDisplayName = string.Empty;

    [ObservableProperty]
    private string _newTenantClientId = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchAppRegistrationsCommand))]
    private string _newTenantAppRegistrationName = string.Empty;

    [ObservableProperty]
    private string _lookupStatus = string.Empty;

    [ObservableProperty]
    private string _appRegistrationSearchStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LookupDomainCommand))]
    private bool _isLookingUpDomain;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchAppRegistrationsCommand))]
    private bool _isSearchingAppRegistrations;

    // Add-tenant mode. Default is Windows-account sign-in — the manual
    // inputs are hidden and the primary button reads "Sign in with Windows
    // account". When the user picks "Manual setup" from the split-button
    // dropdown, the manual inputs reveal and the same primary button
    // becomes the save action for those inputs.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualMode))]
    [NotifyPropertyChangedFor(nameof(IsEmailMode))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionLabel))]
    [NotifyPropertyChangedFor(nameof(AddTenantHint))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private TenantAddMode _addMode = TenantAddMode.Windows;

    public bool IsManualMode => AddMode == TenantAddMode.Manual;
    public bool IsEmailMode => AddMode == TenantAddMode.Email;

    // Non-null while the user is editing an existing tenant via the manual
    // panel. Drives the PrimaryAction dispatch (save existing vs add new),
    // the button label, and the read-only state of the Tenant ID field
    // (the tenant's identity is immutable on edit; only DisplayName and
    // ClientId can change).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingTenant))]
    [NotifyPropertyChangedFor(nameof(PrimaryActionLabel))]
    [NotifyPropertyChangedFor(nameof(IsTenantIdReadOnly))]
    [NotifyPropertyChangedFor(nameof(EditTenantBannerText))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelEditCommand))]
    private Tenant? _editingTenant;

    public bool IsEditingTenant => EditingTenant is not null;
    public bool IsTenantIdReadOnly => IsEditingTenant;

    public string EditTenantBannerText => EditingTenant is null
        ? string.Empty
        : $"Editing {EditingTenant.DisplayName} ({EditingTenant.TenantId}). Tenant ID is locked; change Display name or Client ID and click Save changes.";

    public string PrimaryActionLabel => IsEditingTenant
        ? "Save changes"
        : IsManualMode
            ? "Save tenant"
            : IsEmailMode
                ? "Sign in with email"
                : "Sign in with Windows account";

    // One-line hint above the primary button. Tells the user what the
    // current mode will do — varies because the three modes behave
    // visibly differently (no UI / broker picker / typed form).
    public string AddTenantHint => IsManualMode
        ? "Type the tenant ID and any optional values below. Click Save tenant to validate via Graph /me."
        : IsEmailMode
            ? "Opens the Windows broker picker so you can enter an email, or pick another work/school account you've added in Windows Settings."
            : "Auto-detects from your active Windows session. Tenant ID and app registration are filled in automatically.";

    public ObservableCollection<AppRegistrationInfo> AppRegistrationResults { get; } = new();

    // Drives the search-results ListBox's Visibility. We set this explicitly
    // around AppRegistrationResults mutations rather than path-binding to
    // AppRegistrationResults.Count, because WPF's nested-path binding on
    // an ObservableCollection's Count can miss the very first 0→1
    // transition in some layout orders — leaving the listbox collapsed
    // when there's exactly one match.
    [ObservableProperty]
    private bool _hasAppRegistrationResults;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTenantCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelAddTenantCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInWithWindowsCommand))]
    [NotifyCanExecuteChangedFor(nameof(PrimaryActionCommand))]
    private bool _isAddingTenant;

    [ObservableProperty]
    private string _addTenantStatus = string.Empty;

    // True when AddTenantStatus carries a failure message — the XAML
    // colors the line with Brush.Status.Error so the user notices.
    [ObservableProperty]
    private bool _addTenantStatusIsError;

    [ObservableProperty]
    private string _extensionStatus = string.Empty;

    // ─── Available plugins (NuGet tag search) state ─────────────────────
    // Displayed list — installed plugins filtered out, client-side text
    // filter applied. Updated in RefreshAvailableList().
    public ObservableCollection<NuGetPluginEntry> AvailableOnlinePlugins { get; } = new();

    // Raw fetch result before installed-filter and text-filter are applied.
    // Null until the first successful fetch. Persists across expand/collapse
    // cycles so re-opening the Available section doesn't re-hit the network
    // (the cache in NuGetPluginFeed has its own 60s TTL on top).
    private IReadOnlyList<NuGetPluginEntry>? _fetchedAvailablePlugins;

    // Client-side filter — substring match against DisplayName / Description
    // / Publisher. Changing it just re-applies the filter; never re-fetches.
    [ObservableProperty]
    private string _onlinePluginFilter = string.Empty;

    [ObservableProperty]
    private bool _includeOnlinePrereleases = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshOnlinePluginsCommand))]
    private bool _isBrowsingPlugins;

    [ObservableProperty]
    private string _onlinePluginsStatus = string.Empty;

    [ObservableProperty]
    private bool _hasOnlinePluginsListed;

    // Expander state. Opening it for the first time kicks off the initial
    // fetch (see OnIsAvailablePluginsExpandedChanged); subsequent
    // expand/collapse cycles are free.
    [ObservableProperty]
    private bool _isAvailablePluginsExpanded;

    // Empty-state copy shown in the Available expander when the list is
    // empty for one of: still loading, nothing on nuget.org, filter
    // matched zero, all already installed. Hidden when the list is
    // populated.
    [ObservableProperty]
    private string _availableEmptyMessage = string.Empty;

    [ObservableProperty]
    private bool _hasAvailableEmptyMessage;

    // Status messaging for per-tenant actions (Fix Permissions, Create App
    // Registration). Surfaced below the Tenants list.
    [ObservableProperty]
    private string _tenantActionStatus = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FixPermissionsCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateAppRegistrationCommand))]
    [NotifyCanExecuteChangedFor(nameof(SignInToTenantCommand))]
    private bool _isPerformingTenantAction;

    // Set true by TrayIcon when Settings is opened from the admin menu;
    // gates the visibility of Fix Permissions and Create App Registration
    // controls in the tenant list. The XAML binds Visibility through the
    // BoolToVisibility converter, so flipping this property updates the
    // UI without rebuilding the window.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WindowTitle))]
    private bool _isAdminMode;

    public string WindowTitle => IsAdminMode ? "Settings — Administrator" : "Settings";

    // Two-way bound to the "Launch at Windows sign-in" checkbox. Persists
    // by writing HKCU\…\Run via IStartupManager whenever the user toggles
    // it. Initial value is read from the registry in the ctor (see the
    // _suppressLaunchAtStartupCommit guard for why the first set doesn't
    // round-trip back through Enable/Disable).
    [ObservableProperty]
    private bool _launchAtStartup;

    partial void OnLaunchAtStartupChanged(bool value)
    {
        if (_suppressLaunchAtStartupCommit) return;
        try
        {
            if (value) _startupManager.Enable();
            else _startupManager.Disable();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to {Op} launch-at-startup registration.", value ? "enable" : "disable");
        }
    }

    // Holds the just-added tenant when its Add Tenant flow couldn't find
    // an app registration matching AppRegistrationName, so the UI can
    // offer a one-click "Create app registration" follow-up. Cleared
    // once the Create flow completes (or when the next tenant add
    // resolves to an existing app reg).
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTenantNeedingAppReg))]
    [NotifyPropertyChangedFor(nameof(CreateAppRegistrationPromptText))]
    private Tenant? _tenantNeedingAppReg;

    public bool HasTenantNeedingAppReg => TenantNeedingAppReg is not null;

    public string CreateAppRegistrationPromptText => TenantNeedingAppReg is null
        ? string.Empty
        : $"No app registration named \"{_authOptions.AppRegistrationName}\" was found in {TenantNeedingAppReg.DisplayName}.";

    public ObservableCollection<Tenant> Tenants { get; } = new();
    public ObservableCollection<InstalledExtension> InstalledExtensions { get; } = new();
    public ObservableCollection<PluginConfigViewModel> PluginConfigs { get; } = new();

    private readonly IPluginConfigStore _pluginConfigStore;

    private readonly IPluginSignatureVerifier _signatureVerifier;
    private readonly PluginOptions _pluginOptions;

    public SettingsViewModel(
        IUpdateService updateService,
        IGraphMeClient graphMeClient,
        ITenantStore tenantStore,
        ICredentialFactory credentialFactory,
        IExtensionInstaller extensionInstaller,
        INuGetPluginFeed pluginRegistry,
        IPackageSecurityScanner packageSecurityScanner,
        IFileDialogService fileDialogService,
        IPluginLoader pluginLoader,
        IPluginConfigStore pluginConfigStore,
        IPluginSignatureVerifier signatureVerifier,
        IOpenIdConfigClient oidc,
        IAppRegistrationDiscovery appRegistrationDiscovery,
        IAppRegistrationPermissions appRegistrationPermissions,
        IAppRegistrationProvisioning appRegistrationProvisioning,
        INotifier notifier,
        ITenantReadinessTracker readiness,
        IWindowsAccountSignInService windowsSignIn,
        IGraphOrganizationClient organizationInfo,
        IStartupManager startupManager,
        IOptions<AuthOptions> authOptions,
        IOptions<PluginOptions> pluginOptions,
        ILogger<SettingsViewModel> logger)
    {
        _updateService = updateService;
        _graphMeClient = graphMeClient;
        _tenantStore = tenantStore;
        _credentialFactory = credentialFactory;
        _extensionInstaller = extensionInstaller;
        _nuGetFeed = pluginRegistry;
        _packageSecurityScanner = packageSecurityScanner;
        _fileDialogService = fileDialogService;
        _pluginLoader = pluginLoader;
        _pluginConfigStore = pluginConfigStore;
        _signatureVerifier = signatureVerifier;
        _oidc = oidc;
        _appRegistrationDiscovery = appRegistrationDiscovery;
        _appRegistrationPermissions = appRegistrationPermissions;
        _appRegistrationProvisioning = appRegistrationProvisioning;
        _notifier = notifier;
        _readiness = readiness;
        _windowsSignIn = windowsSignIn;
        _organizationInfo = organizationInfo;
        _startupManager = startupManager;
        _authOptions = authOptions.Value;
        _pluginOptions = pluginOptions.Value;
        _logger = logger;

        VersionDisplay = $"Version {_updateService.CurrentVersionDisplay}";

        // Seed update state from whatever the service already detected
        // before Settings was opened — covers the case where the
        // startup check finished before the user navigated here.
        if (!string.IsNullOrEmpty(_updateService.PendingUpdateVersion))
        {
            PendingUpdateVersion = _updateService.PendingUpdateVersion;
            IsUpdateAvailable = true;
        }
        _updateService.UpdateAvailable += OnUpdateAvailable;

        foreach (var tenant in _tenantStore.GetAll())
        {
            Tenants.Add(tenant);
        }

        RefreshInstalledExtensions();
        RefreshPluginConfigs();

        // Seed the checkbox from the registry without triggering a write-back.
        // A read failure (locked-down user hive, etc.) leaves the box
        // unchecked rather than crashing the Settings window.
        try
        {
            _suppressLaunchAtStartupCommit = true;
            LaunchAtStartup = _startupManager.IsEnabled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read launch-at-startup state from registry.");
        }
        finally
        {
            _suppressLaunchAtStartupCommit = false;
        }
    }

    private void OnUpdateAvailable(string version)
    {
        // Marshal to the UI dispatcher because INotifyPropertyChanged
        // subscribers (XAML bindings) must run on the WPF thread.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            PendingUpdateVersion = version;
            IsUpdateAvailable = true;
        }
        else
        {
            dispatcher.BeginInvoke(new Action(() =>
            {
                PendingUpdateVersion = version;
                IsUpdateAvailable = true;
            }));
        }
    }

    private void RefreshPluginConfigs()
    {
        PluginConfigs.Clear();

        var tenants = _tenantStore.GetAll().ToArray();

        foreach (var loaded in _pluginLoader.LoadedPlugins)
        {
            var plugin = loaded.Plugin;
            var configurable = plugin as IPluginConfigurable;
            object? customView = null;
            try { customView = configurable?.BuildSettingsView(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {Id} threw from BuildSettingsView().", plugin.Id);
            }

            var vm = new PluginConfigViewModel(
                pluginId: plugin.Id,
                displayName: plugin.DisplayName,
                version: plugin.Version,
                isConfigurable: configurable is not null,
                customView: customView);

            var disabled = _pluginConfigStore.GetDisabledTenants(plugin.Id);
            foreach (var t in tenants)
            {
                var captured = t;
                var pluginIdCaptured = plugin.Id;
                vm.Tenants.Add(new PluginTenantToggleViewModel(
                    tenantId: t.TenantId,
                    displayName: t.DisplayName,
                    isEnabled: !disabled.Contains(t.TenantId),
                    commit: enabled =>
                        _pluginConfigStore.SetTenantEnabled(pluginIdCaptured, captured.TenantId, enabled)));
            }

            if (configurable is not null)
            {
                var values = configurable.Values;
                var pluginIdCaptured = plugin.Id;
                var configurableCaptured = configurable;
                foreach (var option in configurable.Options)
                {
                    values.TryGetValue(option.Key, out var current);
                    vm.Options.Add(new PluginOptionViewModel(
                        definition: option,
                        initialValue: current,
                        commit: (key, value) =>
                        {
                            try { configurableCaptured.SetValue(key, value); }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Plugin {Id} rejected SetValue({Key}).", pluginIdCaptured, key);
                            }
                        }));
                }
            }

            PluginConfigs.Add(vm);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesAsync()
    {
        IsBusy = true;
        UpdateStatus = "Checking…";
        try
        {
            UpdateStatus = await _updateService.CheckAndApplyAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Bound to the blue update banner across the top of the Settings
    // window. Calls into Velopack which downloads (if needed) and
    // restarts the process into the new version. The "Restarting…"
    // return message will only be briefly visible — the process exits
    // shortly after.
    [RelayCommand(CanExecute = nameof(CanApplyPendingUpdate))]
    private async Task ApplyPendingUpdateAsync()
    {
        IsBusy = true;
        UpdateStatus = "Installing update…";
        try
        {
            UpdateStatus = await _updateService.CheckAndApplyAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanApplyPendingUpdate() => !IsBusy && IsUpdateAvailable;

    // Dispatched from the single split-button. Routes to the right async
    // action so the XAML can bind one Command + one Content regardless of
    // which mode the user is in.
    [RelayCommand(CanExecute = nameof(CanInvokePrimaryAction))]
    private async Task PrimaryActionAsync()
    {
        if (IsEditingTenant)
        {
            await SaveEditedTenantAsync();
            return;
        }

        switch (AddMode)
        {
            case TenantAddMode.Manual:
                await AddTenantAsync();
                break;
            case TenantAddMode.Email:
                await SignInWithEmailAsync();
                break;
            case TenantAddMode.Windows:
            default:
                await SignInWithWindowsAsync();
                break;
        }
    }

    private bool CanInvokePrimaryAction()
    {
        if (IsEditingTenant)
        {
            // Tenant ID is locked during edit; only require it to be non-empty
            // (it always is, since we pre-populated it).
            return !string.IsNullOrWhiteSpace(NewTenantId) && !IsAddingTenant;
        }
        return AddMode switch
        {
            TenantAddMode.Manual => CanAddTenant(),
            TenantAddMode.Email => CanSignInWithWindows(),
            _ => CanSignInWithWindows(),
        };
    }

    // Dropdown selection both flips the mode AND, for the sign-in modes,
    // kicks off the action immediately — the user already expressed intent
    // by picking that entry, so making them click Save tenant again is
    // pointless. Manual mode is the exception: it just opens the form so
    // the user can fill it in before saving.
    //
    // Re-picking the mode that's already active is suppressed and surfaces
    // a passive notification, so the user gets feedback that the click was
    // recognized but is intentionally a no-op (avoids accidental duplicate
    // broker prompts when the menu is mis-clicked).
    [RelayCommand]
    private async Task SetModeAsync(string? mode)
    {
        var next = mode?.ToLowerInvariant() switch
        {
            "manual" => TenantAddMode.Manual,
            "email" => TenantAddMode.Email,
            _ => TenantAddMode.Windows,
        };

        if (next == AddMode)
        {
            var label = next switch
            {
                TenantAddMode.Email => "Sign in with email",
                TenantAddMode.Manual => "Manual setup",
                _ => "Sign in with Windows account",
            };
            _ = _notifier.ShowAsync(
                new InformationRequest(
                    "Already in this mode",
                    $"\"{label}\" is already the current Add Tenant mode. Pick a different option, or click the primary button to retry the action."),
                CancellationToken.None);
            return;
        }

        AddMode = next;

        switch (next)
        {
            case TenantAddMode.Email:
                await SignInWithEmailAsync();
                break;
            case TenantAddMode.Windows:
                await SignInWithWindowsAsync();
                break;
            case TenantAddMode.Manual:
                // No-op — manual mode reveals the form; the user fills it
                // in and clicks Save tenant.
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSearchAppRegistrations))]
    private async Task SearchAppRegistrationsAsync()
    {
        var tenantId = NewTenantId.Trim();
        var prefix = NewTenantAppRegistrationName.Trim();
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            AppRegistrationSearchStatus = "Resolve a tenant first (Look up by domain).";
            return;
        }

        IsSearchingAppRegistrations = true;
        AppRegistrationSearchStatus = "Searching… (sign-in may open if no cached token)";
        AppRegistrationResults.Clear();
        HasAppRegistrationResults = false;
        try
        {
            var results = await _appRegistrationDiscovery.SearchByDisplayNameAsync(tenantId, prefix, top: 20, CancellationToken.None);
            foreach (var r in results) AppRegistrationResults.Add(r);
            HasAppRegistrationResults = AppRegistrationResults.Count > 0;
            AppRegistrationSearchStatus = results.Count == 0
                ? $"No app registrations found starting with \"{prefix}\"."
                : $"{results.Count} match{(results.Count == 1 ? string.Empty : "es")} — pick one to fill Client ID.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "App registration search failed for tenant {TenantId} prefix '{Prefix}'.", tenantId, prefix);
            AppRegistrationSearchStatus = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearchingAppRegistrations = false;
        }
    }

    public void SelectAppRegistration(AppRegistrationInfo? selection)
    {
        if (selection is null) return;
        NewTenantClientId = selection.AppId;
        NewTenantAppRegistrationName = selection.DisplayName;
        AppRegistrationSearchStatus = $"Selected {selection.DisplayName} ({selection.AppId}).";
    }

    [RelayCommand(CanExecute = nameof(CanLookupDomain))]
    private async Task LookupDomainAsync()
    {
        var domain = NewTenantDomain.Trim();
        IsLookingUpDomain = true;
        LookupStatus = $"Looking up {domain}…";
        try
        {
            var result = await _oidc.DiscoverAsync(domain, CancellationToken.None);
            if (result is null)
            {
                LookupStatus = $"No Entra tenant found for {domain}.";
                return;
            }

            NewTenantId = result.TenantId;
            if (string.IsNullOrWhiteSpace(NewTenantDisplayName))
            {
                NewTenantDisplayName = domain;
            }

            LookupStatus = $"Resolved tenant {result.TenantId} ({result.TenantRegionScope ?? "unknown region"}).";
            _logger.LogInformation("Resolved domain {Domain} to tenant {TenantId}.", domain, result.TenantId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OIDC discovery failed for {Domain}.", domain);
            LookupStatus = $"Lookup failed: {ex.Message}";
        }
        finally
        {
            IsLookingUpDomain = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanSignInWithWindows))]
    private async Task SignInWithWindowsAsync()
    {
        IsAddingTenant = true;
        AddTenantStatusIsError = false;
        AddTenantStatus = "Opening Windows account picker…";
        try
        {
            var account = await _windowsSignIn.SignInAsync(CancellationToken.None);
            await CompleteSignInAsync(account, source: "Windows account picker");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign in with Windows failed.");
            AddTenantStatusIsError = true;
            AddTenantStatus = $"Sign in failed: {ex.Message}";
        }
        finally
        {
            IsAddingTenant = false;
        }
    }

    // "Sign in with email" mode. Always opens the WAM broker picker —
    // which lets the user type an email address or pick from any work/
    // school accounts they've added via Windows Settings → Accounts →
    // Email & accounts. Useful when the tenant they want isn't the
    // active Windows session.
    private async Task SignInWithEmailAsync()
    {
        IsAddingTenant = true;
        AddTenantStatusIsError = false;
        AddTenantStatus = "Opening email sign-in picker…";
        try
        {
            var account = await _windowsSignIn.SignInWithPickerAsync(loginHint: null, CancellationToken.None);
            await CompleteSignInAsync(account, source: "email sign-in");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sign in with email failed.");
            AddTenantStatusIsError = true;
            AddTenantStatus = $"Sign in failed: {ex.Message}";
        }
        finally
        {
            IsAddingTenant = false;
        }
    }

    // Shared post-sign-in flow: OIDC discovery, tenant save, Graph org
    // lookup for display name, app-reg auto-discovery + broker URI prep,
    // ready-mark, plugin-config rebuild. Invoked by both the Windows
    // auto-detect path and the email-picker path with the same
    // WindowsAccount shape; the only difference is the originating
    // `source` string used in the success message and log line.
    private async Task CompleteSignInAsync(WindowsAccount account, string source)
    {
        {
            // 1. OIDC discovery on the user's UPN domain. The broker token
            //    already gave us the tenant ID, but running discovery is a
            //    cheap extra check that the domain is in fact attached to
            //    that tenant — and it surfaces a friendlier display name.
            var resolvedDisplayName = account.DisplayName;
            if (!string.IsNullOrWhiteSpace(account.Domain))
            {
                try
                {
                    AddTenantStatus = $"Resolving {account.Domain}…";
                    var discovery = await _oidc.DiscoverAsync(account.Domain, CancellationToken.None);
                    if (discovery is not null
                        && !string.Equals(discovery.TenantId, account.TenantId, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogInformation(
                            "OIDC discovery for {Domain} returned tenant {DiscoveryTenantId}; broker reported {BrokerTenantId}. Using broker value.",
                            account.Domain, discovery.TenantId, account.TenantId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "OIDC discovery for {Domain} failed; continuing.", account.Domain);
                }
            }

            // 2. Save the tenant up-front with ClientId=null so the next
            //    Graph call (the app-registration search) can authenticate
            //    via CredentialFactory using the global App:Auth:ClientId.
            //    SignInEmail captures the UPN the user signed in with so
            //    future readiness probes / sign-in prompts can pre-fill
            //    the broker's LoginHint and target the same account.
            var tenant = new Tenant(
                account.TenantId,
                resolvedDisplayName,
                ClientId: null,
                SignInEmail: string.IsNullOrWhiteSpace(account.UserPrincipalName) ? null : account.UserPrincipalName,
                ProbeDisabled: false);
            await _tenantStore.AddOrUpdateAsync(tenant, CancellationToken.None);
            _credentialFactory.Invalidate(account.TenantId);

            // 2b. Ask Graph /organization for the tenant's friendly display
            //     name ("Contoso Inc.") using CredentialFactory's broker
            //     credential, which silently re-uses the user's Windows
            //     session via UseDefaultBrokerAccount=true. Falls back to
            //     /me companyName, then to the UPN, all without UI.
            _logger.LogInformation(
                "Querying Graph /organization to label tenant {TenantId}.",
                account.TenantId);
            AddTenantStatus = "Resolving tenant name…";
            var orgDisplayName = await _organizationInfo.GetDisplayNameAsync(account.TenantId, CancellationToken.None);
            if (!string.IsNullOrWhiteSpace(orgDisplayName))
            {
                resolvedDisplayName = orgDisplayName!;
                tenant = tenant with { DisplayName = resolvedDisplayName };
                await _tenantStore.AddOrUpdateAsync(tenant, CancellationToken.None);
                _logger.LogInformation(
                    "Updated tenant {TenantId} display name to {Name}.",
                    account.TenantId, resolvedDisplayName);
            }
            else
            {
                _logger.LogWarning(
                    "Graph could not provide a display name for tenant {TenantId}; keeping UPN {Upn}.",
                    account.TenantId, resolvedDisplayName);
            }

            // 3. Auto-discover a dedicated app registration by display name
            //    (default "AzureTray", configurable in appsettings.json).
            //    If exactly the configured name exists in the tenant, store
            //    its appId on the tenant so future sign-ins use the dedicated
            //    app instead of the global ClientId.
            var appRegName = _authOptions.AppRegistrationName;
            if (!string.IsNullOrWhiteSpace(appRegName))
            {
                try
                {
                    AddTenantStatus = $"Looking up app registration \"{appRegName}\"…";
                    var found = await _appRegistrationDiscovery.FindByDisplayNameAsync(
                        account.TenantId, appRegName, CancellationToken.None);
                    if (found is not null)
                    {
                        _logger.LogInformation(
                            "Auto-discovered app registration {AppRegName} ({AppId}) in tenant {TenantId}.",
                            appRegName, found.AppId, account.TenantId);

                        // Provision the WAM broker redirect URI on the app
                        // registration *before* we switch the tenant over to
                        // it. Until we save it, CredentialFactory's
                        // ResolveClientId falls back to the Azure CLI public
                        // client (which already has its own broker URI
                        // registered), so the PATCH itself can authenticate
                        // successfully via the broker. Without this order,
                        // the PATCH tries to broker-auth as the new app and
                        // hits WAM 3399614473 (ApiContractViolation) — the
                        // very thing we're trying to fix.
                        var brokerProvisioned = false;
                        try
                        {
                            AddTenantStatus = "Configuring app registration for silent sign-in…";
                            brokerProvisioned = await _appRegistrationProvisioning.EnsureBrokerRedirectUriAsync(
                                account.TenantId, found.AppId, CancellationToken.None);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Could not add the WAM broker redirect URI to app {AppId} in tenant {TenantId}. Cold-start silent SSO will likely fail until an admin adds 'ms-appx-web://microsoft.aad.brokerplugin/{AppId}' to the app registration manually.",
                                found.AppId, account.TenantId, found.AppId);
                        }

                        if (brokerProvisioned)
                        {
                            tenant = tenant with { DisplayName = resolvedDisplayName, ClientId = found.AppId };
                            await _tenantStore.AddOrUpdateAsync(tenant, CancellationToken.None);
                            _credentialFactory.Invalidate(account.TenantId);
                        }
                        else
                        {
                            // The discovered app reg is unusable for the
                            // broker without its redirect URI. Leave the
                            // tenant on the Azure CLI fallback so cold-start
                            // sign-in works rather than failing every launch.
                            _logger.LogWarning(
                                "Falling back to the Azure CLI public client for tenant {TenantId}; the discovered app {AppId} cannot be used for broker SSO until its publicClient.redirectUris contains 'ms-appx-web://microsoft.aad.brokerplugin/{AppId}'.",
                                account.TenantId, found.AppId, found.AppId);
                        }
                    }
                    else
                    {
                        _logger.LogInformation(
                            "No app registration named {AppRegName} in tenant {TenantId}; using the global App:Auth:ClientId.",
                            appRegName, account.TenantId);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "App-registration lookup failed for tenant {TenantId}; continuing with the global ClientId.", account.TenantId);
                }
            }

            _readiness.MarkReady(new PluginTenant(account.TenantId, resolvedDisplayName));

            var existing = Tenants.FirstOrDefault(t =>
                string.Equals(t.TenantId, account.TenantId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Tenants.Remove(existing);
            }
            Tenants.Add(tenant);

            // If discovery couldn't find an app registration with the
            // configured name, expose the contextual "Create app
            // registration" button. Otherwise clear any prior prompt.
            TenantNeedingAppReg = tenant.ClientId is null ? tenant : null;

            // Rebuild the per-plugin tenant toggles so the new tenant
            // appears as a configurable row in each plugin's section
            // without needing to close + reopen Settings.
            RefreshPluginConfigs();

            NewTenantDomain = string.Empty;
            NewTenantId = string.Empty;
            NewTenantDisplayName = string.Empty;
            NewTenantClientId = string.Empty;
            NewTenantAppRegistrationName = string.Empty;
            LookupStatus = string.Empty;

            var detail = string.IsNullOrEmpty(account.UserPrincipalName)
                ? string.Empty
                : $" ({account.UserPrincipalName})";
            AddTenantStatus = tenant.ClientId is null
                ? $"Added {resolvedDisplayName}{detail} — using global App:Auth:ClientId."
                : $"Added {resolvedDisplayName}{detail} — bound to app registration {tenant.ClientId}.";

            _logger.LogInformation(
                "Added tenant {TenantId} as {DisplayName} via {Source} (ClientId={ClientId}).",
                account.TenantId, resolvedDisplayName, source, tenant.ClientId ?? "global");
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddTenant))]
    private async Task AddTenantAsync()
    {
        var tenantId = NewTenantId.Trim();
        var displayName = string.IsNullOrWhiteSpace(NewTenantDisplayName) ? tenantId : NewTenantDisplayName.Trim();
        var rawClientId = NewTenantClientId.Trim();
        var appRegName = NewTenantAppRegistrationName.Trim();
        string? clientId;

        if (!string.IsNullOrWhiteSpace(rawClientId))
        {
            if (!Guid.TryParse(rawClientId, out _))
            {
                AddTenantStatusIsError = true;
                AddTenantStatus = "Client ID must be a GUID. Clear it to use the app-registration name lookup or the global App:Auth:ClientId.";
                return;
            }
            clientId = rawClientId;
        }
        else
        {
            // ClientId stays null; the credential factory falls back to the
            // global App:Auth:ClientId, and finally to the Azure CLI public
            // client. The app-reg lookup below uses whichever resolves.
            clientId = null;
        }

        var tenant = new Tenant(tenantId, displayName, clientId, SignInEmail: null, ProbeDisabled: false);

        var cts = new CancellationTokenSource(SignInTimeout);
        _addTenantCts = cts;
        IsAddingTenant = true;
        AddTenantStatusIsError = false;
        AddTenantStatus = "Signing in… (click Cancel to abort)";

        try
        {
            await _tenantStore.AddOrUpdateAsync(tenant, cts.Token);
            _credentialFactory.Invalidate(tenantId);

            // The user just clicked Save tenant — proactively drive the
            // broker so the per-tenant MSAL cache is populated before any
            // Graph call. Without this the first /me call throws
            // AuthenticationRequiredException because the silent
            // credential has no cached token to refresh.
            AddTenantStatus = "Signing in to the tenant… (broker dialog may open)";
            await _credentialFactory.SignInAsync(tenantId, cts.Token);

            var me = await _graphMeClient.GetMeAsync(tenantId, cts.Token);

            // Capture the signed-in UPN so subsequent sign-in prompts can
            // pre-fill MSAL's LoginHint to this account.
            if (!string.IsNullOrWhiteSpace(me.UserPrincipalName))
            {
                tenant = tenant with { SignInEmail = me.UserPrincipalName };
            }

            // If the user supplied an app-registration name (and no explicit
            // ClientId), look up its appId now that we're signed in and
            // persist it so future sign-ins use the dedicated app.
            if (clientId is null && !string.IsNullOrWhiteSpace(appRegName))
            {
                var found = await _appRegistrationDiscovery.FindByDisplayNameAsync(tenantId, appRegName, cts.Token);
                if (found is not null)
                {
                    tenant = tenant with { ClientId = found.AppId };
                    _credentialFactory.Invalidate(tenantId);
                    _logger.LogInformation(
                        "Resolved app registration '{Name}' to appId {AppId} in tenant {TenantId}.",
                        appRegName, found.AppId, tenantId);
                }
                else
                {
                    AddTenantStatus = $"App registration '{appRegName}' not found in tenant {displayName}. Using the global App:Auth:ClientId instead.";
                }
            }

            // Persist whatever we ended up with (display name update + email
            // + possibly resolved client id) before any UI bookkeeping.
            await _tenantStore.AddOrUpdateAsync(tenant, cts.Token);

            var existing = Tenants.FirstOrDefault(t =>
                string.Equals(t.TenantId, tenantId, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                Tenants.Remove(existing);
            }
            Tenants.Add(tenant);

            // The Graph /me call above guarantees a token was acquired, which
            // is exactly the "ready" condition the readiness tracker exposes.
            // Marking ready here lets every loaded plugin start watching this
            // tenant immediately without waiting for the next probe cycle.
            _readiness.MarkReady(new PluginTenant(tenantId, displayName));

            // If the manual flow couldn't bind the tenant to a dedicated
            // app reg (no clientId typed in, or named lookup failed),
            // surface the contextual Create button.
            TenantNeedingAppReg = tenant.ClientId is null ? tenant : null;

            // Rebuild the per-plugin tenant toggles so the new tenant
            // appears as a configurable row in each plugin's section
            // without needing to close + reopen Settings.
            RefreshPluginConfigs();

            NewTenantDomain = string.Empty;
            NewTenantId = string.Empty;
            NewTenantDisplayName = string.Empty;
            NewTenantClientId = string.Empty;
            NewTenantAppRegistrationName = string.Empty;
            LookupStatus = string.Empty;

            // Collapse the manual form once the save succeeds — the user
            // is done with it and the saved tenant is now in the list
            // above. Subsequent adds default back to Windows sign-in.
            AddMode = TenantAddMode.Windows;

            if (string.IsNullOrWhiteSpace(AddTenantStatus) || !AddTenantStatus.StartsWith("App registration", StringComparison.Ordinal))
            {
                AddTenantStatus = $"Added {displayName}: signed in as {me.DisplayName} ({me.UserPrincipalName}).";
            }
            _logger.LogInformation("Added tenant {TenantId} as {Upn}.", tenantId, me.UserPrincipalName);
        }
        catch (OperationCanceledException)
        {
            await _tenantStore.RemoveAsync(tenantId, CancellationToken.None);
            _credentialFactory.Invalidate(tenantId);

            AddTenantStatus = cts.Token.IsCancellationRequested
                ? "Sign-in cancelled or timed out. You can edit the fields and try again."
                : "Sign-in cancelled.";

            _logger.LogInformation(
                "Add-tenant cancelled or timed out for tenant {TenantId}; reverted store entry.",
                tenantId);
        }
        catch (Exception ex)
        {
            await _tenantStore.RemoveAsync(tenantId, CancellationToken.None);
            _credentialFactory.Invalidate(tenantId);

            _logger.LogWarning(ex, "Sign-in failed for tenant {TenantId}; reverted store entry.", tenantId);
            AddTenantStatusIsError = true;
            AddTenantStatus = $"Sign-in failed: {ex.Message}";
        }
        finally
        {
            _addTenantCts = null;
            IsAddingTenant = false;
            cts.Dispose();
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelAddTenant))]
    private void CancelAddTenant()
    {
        try
        {
            _addTenantCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Raced with the finally block in AddTenantAsync; nothing to do.
        }
    }

    // Drives the per-tenant "Sign in" button that appears on disabled
    // rows. Calls the credential factory's explicit interactive sign-in,
    // and on success clears ProbeDisabled, marks the tenant ready, and
    // updates the in-memory collection so the row's styling refreshes
    // without a Settings reopen.
    [RelayCommand(CanExecute = nameof(CanSignInToTenant))]
    private async Task SignInToTenantAsync(Tenant? tenant)
    {
        if (tenant is null) return;

        try
        {
            IsPerformingTenantAction = true;
            TenantActionStatus = $"Signing in to \"{tenant.DisplayName}\"… (broker dialog may open)";

            await _credentialFactory.SignInAsync(tenant.TenantId, CancellationToken.None);

            // Persist ProbeDisabled=false so subsequent launches skip the
            // notification prompt for this tenant.
            var updated = tenant with { ProbeDisabled = false };
            await _tenantStore.AddOrUpdateAsync(updated, CancellationToken.None);

            var index = Tenants.IndexOf(tenant);
            if (index >= 0)
            {
                Tenants[index] = updated;
            }

            _readiness.MarkReady(new PluginTenant(updated.TenantId, updated.DisplayName));
            RefreshPluginConfigs();

            TenantActionStatus = $"Signed in to \"{updated.DisplayName}\" — tenant re-enabled.";
            _logger.LogInformation(
                "Tenant {TenantId} ({DisplayName}) re-enabled after user-initiated sign-in.",
                updated.TenantId, updated.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Interactive sign-in failed for tenant {TenantId} ({DisplayName}).",
                tenant.TenantId, tenant.DisplayName);
            TenantActionStatus = $"Sign-in failed for \"{tenant.DisplayName}\": {ex.Message}";
        }
        finally
        {
            IsPerformingTenantAction = false;
        }
    }

    private bool CanSignInToTenant(Tenant? tenant)
        => !IsPerformingTenantAction && tenant is not null;

    // Pre-populates the manual add-tenant panel with the selected tenant's
    // values and flips the form into "Edit" mode so the primary button
    // becomes Save changes. Domain lookup, app-reg search, etc. all remain
    // available — only the Tenant ID field locks (immutable identity).
    [RelayCommand]
    private void BeginEditTenant(Tenant? tenant)
    {
        if (tenant is null) return;

        AddMode = TenantAddMode.Manual;
        EditingTenant = tenant;

        NewTenantDomain = string.Empty;
        LookupStatus = string.Empty;
        NewTenantId = tenant.TenantId;
        NewTenantDisplayName = tenant.DisplayName;
        NewTenantClientId = tenant.ClientId ?? string.Empty;
        NewTenantAppRegistrationName = string.Empty;
        AppRegistrationSearchStatus = string.Empty;
        AppRegistrationResults.Clear();
        HasAppRegistrationResults = false;
        AddTenantStatus = string.Empty;
        TenantNeedingAppReg = null;
    }

    private bool CanCancelEdit() => IsEditingTenant && !IsAddingTenant;

    [RelayCommand(CanExecute = nameof(CanCancelEdit))]
    private void CancelEdit()
    {
        EditingTenant = null;
        NewTenantDomain = string.Empty;
        NewTenantId = string.Empty;
        NewTenantDisplayName = string.Empty;
        NewTenantClientId = string.Empty;
        NewTenantAppRegistrationName = string.Empty;
        LookupStatus = string.Empty;
        AppRegistrationSearchStatus = string.Empty;
        AppRegistrationResults.Clear();
        HasAppRegistrationResults = false;
        AddTenantStatus = string.Empty;
    }

    // Persists the user's edits to the existing tenant. TenantId is locked,
    // so this is effectively "rename + rebind to a different (or no) app
    // registration." Avoids the heavyweight Add path's Graph /me probe;
    // we trust the user knows what they're doing.
    private async Task SaveEditedTenantAsync()
    {
        if (EditingTenant is not { } original) return;

        var displayName = string.IsNullOrWhiteSpace(NewTenantDisplayName)
            ? original.DisplayName
            : NewTenantDisplayName.Trim();
        var clientIdInput = NewTenantClientId.Trim();
        var clientId = string.IsNullOrWhiteSpace(clientIdInput) ? null : clientIdInput;
        // Preserve sign-in metadata on edit — only DisplayName and ClientId
        // are user-facing on the form; SignInEmail / ProbeDisabled live on
        // the same record and shouldn't be wiped by a rename.
        var updated = original with { DisplayName = displayName, ClientId = clientId };

        try
        {
            IsAddingTenant = true;
            await _tenantStore.AddOrUpdateAsync(updated, CancellationToken.None);
            _credentialFactory.Invalidate(updated.TenantId);

            var index = Tenants.IndexOf(original);
            if (index >= 0)
            {
                Tenants[index] = updated;
            }

            // Plugin config rows display the tenant's DisplayName — rebuild
            // so any rename surfaces immediately.
            RefreshPluginConfigs();

            var changeSummary = BuildEditChangeSummary(original, updated);
            AddTenantStatus = changeSummary.Length == 0
                ? $"No changes to save for {updated.DisplayName}."
                : $"Saved {updated.DisplayName}: {changeSummary}.";

            _logger.LogInformation(
                "Edited tenant {TenantId}: displayName '{OldName}'→'{NewName}', clientId '{OldClient}'→'{NewClient}'.",
                updated.TenantId, original.DisplayName, updated.DisplayName, original.ClientId ?? "(none)", updated.ClientId ?? "(none)");

            EditingTenant = null;
            NewTenantDomain = string.Empty;
            NewTenantId = string.Empty;
            NewTenantDisplayName = string.Empty;
            NewTenantClientId = string.Empty;
            NewTenantAppRegistrationName = string.Empty;
            AppRegistrationResults.Clear();
            HasAppRegistrationResults = false;

            // Collapse the form on successful save — same behavior as the
            // Add-tenant save path.
            AddMode = TenantAddMode.Windows;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save edits to tenant {TenantId}.", updated.TenantId);
            AddTenantStatusIsError = true;
            AddTenantStatus = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsAddingTenant = false;
        }
    }

    private static string BuildEditChangeSummary(Tenant original, Tenant updated)
    {
        var parts = new List<string>();
        if (!string.Equals(original.DisplayName, updated.DisplayName, StringComparison.Ordinal))
            parts.Add($"display name → \"{updated.DisplayName}\"");
        if (!string.Equals(original.ClientId, updated.ClientId, StringComparison.OrdinalIgnoreCase))
            parts.Add($"client id → {(updated.ClientId is null ? "(global fallback)" : updated.ClientId)}");
        return string.Join("; ", parts);
    }

    [RelayCommand]
    private async Task RemoveTenantAsync(Tenant? tenant)
    {
        if (tenant is null) return;

        await _tenantStore.RemoveAsync(tenant.TenantId, CancellationToken.None);
        _credentialFactory.Invalidate(tenant.TenantId);
        _readiness.MarkRemoved(tenant.TenantId);
        Tenants.Remove(tenant);

        // Drop the per-plugin tenant toggle row for the removed tenant
        // so the stale entry doesn't linger until Settings reopens.
        RefreshPluginConfigs();

        _logger.LogInformation("Removed tenant {TenantId}.", tenant.TenantId);
    }

    // Fixes the tenant's app registration to declare and admin-consent
    // exactly the scopes the host + loaded plugins need. Replace
    // semantics: stale scopes are pruned. Requires the signed-in user
    // to have Global / Application Administrator authority for the
    // PATCH on /applications and the POST on /oauth2PermissionGrants
    // to succeed.
    [RelayCommand(CanExecute = nameof(CanFixPermissions))]
    private async Task FixPermissionsAsync(Tenant? tenant)
    {
        if (tenant is null) return;
        if (string.IsNullOrWhiteSpace(tenant.ClientId))
        {
            TenantActionStatus = $"Tenant '{tenant.DisplayName}' has no dedicated app registration — create one first.";
            return;
        }

        var required = AggregateRequiredPermissions();
        if (required.Count == 0)
        {
            TenantActionStatus = "No permissions to apply — host has no baseline scopes and no plugins are loaded.";
            return;
        }

        try
        {
            IsPerformingTenantAction = true;
            TenantActionStatus = $"Fixing permissions on \"{tenant.DisplayName}\"… (sign-in may open if no cached admin token)";

            var result = await _appRegistrationPermissions.EnsureAsync(
                tenant.TenantId, tenant.ClientId, required, CancellationToken.None);

            TenantActionStatus = FormatFixResult(tenant.DisplayName, result);
            _logger.LogInformation(
                "Fix Permissions on tenant {TenantId} app {AppClientId}: added {Added} scope(s), removed {Removed} stale; grants added {GrantsAdded}, stale grants removed {GrantsRemoved}.",
                tenant.TenantId, tenant.ClientId,
                result.ScopesAdded.Count, result.StaleScopesRemoved,
                result.GrantsAdded.Count, result.StaleGrantsRemoved);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Fix Permissions failed for tenant {TenantId} app {AppClientId}.",
                tenant.TenantId, tenant.ClientId);
            TenantActionStatus = $"Fix Permissions failed for \"{tenant.DisplayName}\": {ex.Message}";
        }
        finally
        {
            IsPerformingTenantAction = false;
        }
    }

    private bool CanFixPermissions(Tenant? tenant)
        => !IsPerformingTenantAction
           && tenant is not null
           && !string.IsNullOrWhiteSpace(tenant.ClientId);

    // Creates a brand-new app registration for the tenant in one step:
    // POST /applications + /servicePrincipals, set the WAM broker URI,
    // pre-populate requiredResourceAccess with all host + plugin scopes,
    // grant admin consent. On success the new clientId is persisted on
    // the tenant so subsequent sign-ins use the dedicated app reg.
    //
    // Only valid for tenants that don't already have a dedicated app
    // registration (those using the Azure CLI public-client fallback).
    [RelayCommand(CanExecute = nameof(CanCreateAppRegistration))]
    private async Task CreateAppRegistrationAsync(Tenant? tenant)
    {
        if (tenant is null) return;
        if (!string.IsNullOrWhiteSpace(tenant.ClientId))
        {
            TenantActionStatus = $"\"{tenant.DisplayName}\" already has a dedicated app registration.";
            return;
        }

        var displayName = _authOptions.AppRegistrationName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            TenantActionStatus = "Configured App:Auth:AppRegistrationName is empty — set it in appsettings.json before creating an app registration.";
            return;
        }

        var required = AggregateRequiredPermissions();

        try
        {
            IsPerformingTenantAction = true;
            TenantActionStatus = $"Creating app registration \"{displayName}\" in \"{tenant.DisplayName}\"… (admin sign-in may open)";

            var result = await _appRegistrationProvisioning.CreateAsync(
                tenant.TenantId, displayName, required, CancellationToken.None);

            // Persist the new clientId on the tenant + invalidate the cached
            // credential so future calls authenticate as the new app reg.
            // Preserve SignInEmail / ProbeDisabled across the rebind.
            var updated = tenant with { ClientId = result.App.AppId };
            await _tenantStore.AddOrUpdateAsync(updated, CancellationToken.None);
            _credentialFactory.Invalidate(tenant.TenantId);

            var index = Tenants.IndexOf(tenant);
            if (index >= 0)
            {
                Tenants[index] = updated;
            }

            // Dismiss the contextual prompt if it was targeting this tenant.
            if (TenantNeedingAppReg is not null
                && string.Equals(TenantNeedingAppReg.TenantId, tenant.TenantId, StringComparison.OrdinalIgnoreCase))
            {
                TenantNeedingAppReg = null;
            }

            TenantActionStatus = FormatCreateResult(updated.DisplayName, displayName, result);
            _logger.LogInformation(
                "Created and persisted app registration {DisplayName} ({AppId}) for tenant {TenantId}: scopes granted {Scopes}, broker URI added {BrokerAdded}.",
                displayName, result.App.AppId, tenant.TenantId, result.ScopesGranted, result.BrokerRedirectUriAdded);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Create App Registration failed for tenant {TenantId}.",
                tenant.TenantId);
            TenantActionStatus = $"Create App Registration failed for \"{tenant.DisplayName}\": {ex.Message}";
        }
        finally
        {
            IsPerformingTenantAction = false;
        }
    }

    private bool CanCreateAppRegistration(Tenant? tenant)
        => !IsPerformingTenantAction
           && tenant is not null
           && string.IsNullOrWhiteSpace(tenant.ClientId);

    private static string FormatCreateResult(string tenantDisplayName, string appDisplayName, AppRegistrationCreateResult result)
    {
        var note = result.BrokerRedirectUriAdded
            ? ""
            : " (WAM broker URI not added — silent SSO may need manual fix-up)";
        return $"Created \"{appDisplayName}\" ({result.App.AppId}) in \"{tenantDisplayName}\", admin-consented {result.ScopesGranted} scope(s){note}.";
    }

    // Aggregates host baseline scopes with every loaded plugin's required
    // permissions. Deduplicates by (resourceAppId, scopeId) so the same
    // scope declared in two places only PATCHes once.
    private List<PluginPermissionRequirement> AggregateRequiredPermissions()
    {
        var seen = new HashSet<(PermissionApi Api, string ScopeId)>();
        var all = new List<PluginPermissionRequirement>();

        void AddRange(IEnumerable<PluginPermissionRequirement> source)
        {
            foreach (var p in source)
            {
                if (seen.Add((p.Api, p.ScopeId))) all.Add(p);
            }
        }

        AddRange(HostRequiredPermissions.All);
        foreach (var loaded in _pluginLoader.LoadedPlugins)
        {
            AddRange(loaded.Plugin.RequiredPermissions);
        }

        return all;
    }

    private static string FormatFixResult(string tenantDisplayName, PermissionFixResult result)
    {
        var parts = new List<string>();
        if (result.ScopesAdded.Count > 0) parts.Add($"added {result.ScopesAdded.Count} scope(s)");
        if (result.GrantsAdded.Count > 0) parts.Add($"consented {result.GrantsAdded.Count} grant(s)");
        if (result.StaleScopesRemoved > 0) parts.Add($"removed {result.StaleScopesRemoved} stale scope(s)");
        if (result.StaleGrantsRemoved > 0) parts.Add($"removed {result.StaleGrantsRemoved} stale grant(s)");

        return parts.Count == 0
            ? $"\"{tenantDisplayName}\" already configured — nothing to do."
            : $"\"{tenantDisplayName}\" updated: {string.Join(", ", parts)}.";
    }

    [RelayCommand]
    private void ConfigureExtension(InstalledExtension? extension)
    {
        if (extension is null || !extension.IsLoaded || string.IsNullOrEmpty(extension.PluginId)) return;

        var vm = PluginConfigs.FirstOrDefault(p =>
            string.Equals(p.PluginId, extension.PluginId, StringComparison.OrdinalIgnoreCase));
        if (vm is null) return;

        var window = new PluginConfigWindow();
        window.Configure(vm, _pluginLoader);
        if (System.Windows.Application.Current?.Windows is { } windows)
        {
            foreach (System.Windows.Window w in windows)
            {
                if (w.IsActive) { window.Owner = w; break; }
            }
        }
        window.Show();
    }

    [RelayCommand]
    private async Task InstallExtensionAsync()
    {
        var sourcePath = _fileDialogService.OpenFile(
            title: "Select plugin .nupkg",
            filter: "Plugin packages (*.nupkg)|*.nupkg");
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        try
        {
            var installed = await _extensionInstaller.InstallFromFileAsync(sourcePath, CancellationToken.None);

            var trustDecision = await EvaluateSignatureTrustAsync(
                packageId: Path.GetFileNameWithoutExtension(sourcePath),
                installedDlls: installed,
                origin: sourcePath,
                // Local file installs bypass the registry's CI checks
                // (source-repo verification, dependency scan, VirusTotal)
                // and the install-time GHSA scan. Be honest about it.
                completedChecks: Array.Empty<string>());
            if (trustDecision == SignatureTrustDecision.Reject)
            {
                await CleanUpFailedInstallAsync(installed);
                RefreshInstalledExtensions();
                return;
            }

            // Hot-load every produced DLL. The loader rejects anything
            // that isn't an ITrayPlugin (bundled transitive deps), so we
            // only count successful loads in the status message.
            string? loadedName = null;
            string? loadedVersion = null;
            foreach (var dllPath in installed)
            {
                var loaded = await _pluginLoader.LoadOneAsync(dllPath, CancellationToken.None);
                if (loaded is not null)
                {
                    loadedName = loaded.Plugin.DisplayName;
                    loadedVersion = loaded.Plugin.Version;
                }
            }

            ExtensionStatus = loadedName is not null
                ? $"Installed and loaded {loadedName} v{loadedVersion}."
                : $"Installed {Path.GetFileName(sourcePath)} but no ITrayPlugin was recognised (see logs).";
            RefreshInstalledExtensions();
            RefreshPluginConfigs();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install extension from {Path}.", sourcePath);
            ExtensionStatus = $"Install failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task UninstallExtensionAsync(InstalledExtension? extension)
    {
        if (extension is null) return;

        try
        {
            // Hot path: unload the plugin first so the collectible ALC
            // releases its mmap, then delete the files.
            if (extension.IsLoaded && !string.IsNullOrWhiteSpace(extension.PluginId))
            {
                await _pluginLoader.UnloadOneAsync(extension.PluginId, CancellationToken.None);
            }

            var deleted = await _extensionInstaller.TryDeleteAsync(extension.FullPath, CancellationToken.None);
            if (!deleted)
            {
                // Files still locked despite ALC unload + retries. Sentinel
                // them for startup cleanup, silently — from the user's
                // perspective the plugin is already gone (unloaded).
                await _extensionInstaller.RequestUninstallAsync(extension.FullPath, CancellationToken.None);
            }

            ExtensionStatus = $"Uninstalled {extension.FileName}.";
            RefreshInstalledExtensions();
            RefreshPluginConfigs();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to uninstall {FileName}.", extension.FileName);
            ExtensionStatus = $"Uninstall failed: {ex.Message}";
        }
    }

    // Explicit user-driven re-fetch. Bypasses the in-memory cache TTL
    // so a freshly-published plugin shows up immediately.
    [RelayCommand(CanExecute = nameof(CanRefreshOnlinePlugins))]
    private Task RefreshOnlinePluginsAsync() => FetchAvailableAsync(forceRefresh: true);

    private bool CanRefreshOnlinePlugins() => !IsBrowsingPlugins;

    // Auto-fetch trigger: first time the Available expander opens, kick
    // off the initial fetch. Subsequent expand/collapse cycles are free —
    // the raw list lives in _fetchedAvailablePlugins.
    partial void OnIsAvailablePluginsExpandedChanged(bool value)
    {
        if (!value) return;
        if (_fetchedAvailablePlugins is not null) return;
        if (IsBrowsingPlugins) return;
        _ = FetchAvailableAsync(forceRefresh: false);
    }

    // Toggling prerelease changes the server-side query, so we need a
    // fresh fetch (not just a re-filter of cached data).
    partial void OnIncludeOnlinePrereleasesChanged(bool value)
    {
        if (!IsAvailablePluginsExpanded) return;
        _ = FetchAvailableAsync(forceRefresh: false);
    }

    // Filter is client-side over the already-fetched list — just re-apply.
    partial void OnOnlinePluginFilterChanged(string value) => RefreshAvailableList();

    private async Task FetchAvailableAsync(bool forceRefresh)
    {
        IsBrowsingPlugins = true;
        OnlinePluginsStatus = "Searching nuget.org…";
        try
        {
            var results = await _nuGetFeed.FetchAsync(
                query: null,
                includePrerelease: IncludeOnlinePrereleases,
                cancellationToken: CancellationToken.None,
                forceRefresh: forceRefresh);

            _fetchedAvailablePlugins = results;
            OnlinePluginsStatus = string.Empty;
            RefreshAvailableList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "NuGet plugin search failed.");
            OnlinePluginsStatus = $"NuGet search failed: {ex.Message}";
        }
        finally
        {
            IsBrowsingPlugins = false;
        }
    }

    // Rebuilds AvailableOnlinePlugins from the raw fetch result, hiding
    // already-installed plugins and applying the user's filter text.
    // Called from: FetchAvailableAsync (fresh data), OnOnlinePluginFilterChanged
    // (filter text changed), RefreshInstalledExtensions wrappers (after
    // install/uninstall — the just-installed plugin should drop out).
    private void RefreshAvailableList()
    {
        AvailableOnlinePlugins.Clear();

        if (_fetchedAvailablePlugins is null)
        {
            // Haven't fetched yet — leave the list empty. The expander's
            // empty-state copy handles the "loading…" / "expand to load"
            // cases.
            HasOnlinePluginsListed = false;
            UpdateAvailableEmptyMessage(matchCount: 0, fetched: false);
            return;
        }

        // Installed lookup: match package id to the InstalledExtension's
        // filename minus .dll. plugins/<id>/<id>.dll → "<id>".
        var installedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var ext in InstalledExtensions)
        {
            var id = Path.GetFileNameWithoutExtension(ext.FileName);
            if (!string.IsNullOrEmpty(id)) installedIds.Add(id);
        }

        var filter = OnlinePluginFilter.Trim();
        foreach (var entry in _fetchedAvailablePlugins)
        {
            if (installedIds.Contains(entry.Id)) continue;
            if (!MatchesFilter(entry, filter)) continue;
            AvailableOnlinePlugins.Add(entry);
        }

        HasOnlinePluginsListed = AvailableOnlinePlugins.Count > 0;
        UpdateAvailableEmptyMessage(matchCount: AvailableOnlinePlugins.Count, fetched: true);
    }

    private static bool MatchesFilter(NuGetPluginEntry entry, string filter)
    {
        if (string.IsNullOrEmpty(filter)) return true;
        return Contains(entry.DisplayName, filter)
            || Contains(entry.Description, filter)
            || Contains(entry.Publisher, filter);

        static bool Contains(string? haystack, string needle)
            => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateAvailableEmptyMessage(int matchCount, bool fetched)
    {
        if (matchCount > 0)
        {
            AvailableEmptyMessage = string.Empty;
            HasAvailableEmptyMessage = false;
            return;
        }

        var raw = _fetchedAvailablePlugins;
        if (!fetched || raw is null)
        {
            AvailableEmptyMessage = IsBrowsingPlugins ? "Loading…" : "Expand to load the list from nuget.org.";
        }
        else if (raw.Count == 0)
        {
            AvailableEmptyMessage = "No plugins on nuget.org carry the proxylayer.azuretray-plugin tag (yet).";
        }
        else if (!string.IsNullOrWhiteSpace(OnlinePluginFilter))
        {
            AvailableEmptyMessage = $"No plugin matches \"{OnlinePluginFilter}\".";
        }
        else
        {
            AvailableEmptyMessage = "All available plugins are already installed.";
        }
        HasAvailableEmptyMessage = true;
    }

    // Surfaces an InformationRequest popup listing the new plugin's
    // RequiredPermissions so the user knows to run Fix Permissions per
    // tenant. Best-effort — failures here don't disturb the install.
    private void NotifyRequiredPermissionsForNewPlugin(NuGetPluginEntry entry)
    {
        try
        {
            var match = _pluginLoader.LoadedPlugins
                .FirstOrDefault(p => string.Equals(p.Plugin.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (match is null) return;

            var scopes = match.Plugin.RequiredPermissions;
            if (scopes.Count == 0) return;

            var summary = string.Join(", ",
                scopes.Select(p => $"{p.ScopeName} ({p.Api})"));
            var message =
                $"{match.Plugin.DisplayName} v{match.Plugin.Version} declares {scopes.Count} required scope(s):\n\n{summary}\n\n" +
                "Open Settings → admin mode and click \"Fix permissions\" on each tenant you want this plugin to operate on.";

            _ = _notifier.ShowAsync(
                new InformationRequest($"{match.Plugin.DisplayName} installed", message),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to surface required-permissions notification for {PluginId}.",
                entry.Id);
        }
    }

    // Downloads and installs the selected registry plugin (highest
    // version offered by the registry, honoring the prerelease
    // checkbox), then hot-loads it without requiring a restart. When
    // the entry carries a nugetPackageId, runs a GHSA vulnerability
    // scan against that coordinate first — High/Critical advisories
    // trigger a Yes/No confirm; lower severities log but don't block.
    [RelayCommand]
    private async Task InstallOnlinePluginAsync(NuGetPluginEntry? entry)
    {
        if (entry is null || entry.Versions.Count == 0) return;
        var version = entry.Versions[0]; // registry orders newest-first

        // Tracks the vetting steps that actually ran for THIS install so
        // the unsigned-prompt can quote them accurately later. Don't
        // include claims about scans that were skipped or failed.
        var completedChecks = new List<string>
        {
            "Discovery filter: package is tagged 'proxylayer.azuretray-plugin' on nuget.org.",
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(entry.NuGetPackageId))
            {
                OnlinePluginsStatus = $"Scanning {entry.NuGetPackageId} {version.Version} for known vulnerabilities…";
                var scan = await _packageSecurityScanner.ScanAsync(entry.NuGetPackageId, version.Version, CancellationToken.None);

                if (!scan.ScanSucceeded)
                {
                    _logger.LogWarning(
                        "Security scan failed for {PackageId} {Version}: {Error}. Proceeding with install.",
                        entry.NuGetPackageId, version.Version, scan.ScanError);
                }
                else if (scan.HasCriticalOrHigh)
                {
                    var advisoriesText = string.Join("\n",
                        scan.Advisories
                            .Where(a => a.Severity is AdvisorySeverity.Critical or AdvisorySeverity.High)
                            .Select(a => $"  • {a.Severity}: {a.Id} — {a.Summary}"));

                    var confirm = await _notifier.ShowAsync(
                        new YesNoRequest(
                            $"Security advisory for {entry.NuGetPackageId}",
                            $"GHSA reports {scan.Advisories.Count} advisor(y/ies) for {entry.NuGetPackageId} {version.Version}. " +
                            $"High or Critical findings:\n\n{advisoriesText}\n\nInstall anyway?"),
                        CancellationToken.None);

                    if (confirm is not YesNoResult yn || !yn.Accepted)
                    {
                        OnlinePluginsStatus = $"Install of {entry.Id} cancelled — {scan.Advisories.Count} advisor(y/ies) on file.";
                        return;
                    }
                }
                else if (scan.HasAny)
                {
                    _logger.LogInformation(
                        "{PackageId} {Version}: {Count} lower-severity advisor(y/ies) — proceeding.",
                        entry.NuGetPackageId, version.Version, scan.Advisories.Count);
                }

                if (scan.ScanSucceeded)
                {
                    completedChecks.Add(scan.HasAny
                        ? $"GHSA install-time scan: {scan.Advisories.Count} lower-severity advisor(y/ies)."
                        : "GHSA install-time scan: no advisories.");
                }
                else
                {
                    completedChecks.Add($"GHSA install-time scan: skipped ({scan.ScanError ?? "lookup error"}).");
                }
            }
            else
            {
                _logger.LogInformation(
                    "{PluginId} has no nugetPackageId — skipping GHSA scan. Binary will still be checksum-verified if the registry entry carries a hash.",
                    entry.Id);
                completedChecks.Add("GHSA install-time scan: skipped (no nugetPackageId on registry entry).");
            }

            OnlinePluginsStatus = $"Installing {entry.DisplayName} {version.Version}…";
            var installed = await _extensionInstaller.InstallFromUrlAsync(
                entry.Id,
                version.DownloadUrl,
                version.ChecksumSha256,
                CancellationToken.None);

            completedChecks.Add(string.IsNullOrWhiteSpace(version.ChecksumSha256)
                ? "SHA-256 checksum: not pinned in registry entry."
                : "SHA-256 checksum: verified against registry entry.");

            var trustDecision = await EvaluateSignatureTrustAsync(
                packageId: entry.Id,
                installedDlls: installed,
                origin: entry.SourceRepo ?? version.DownloadUrl,
                completedChecks: completedChecks);
            if (trustDecision == SignatureTrustDecision.Reject)
            {
                await CleanUpFailedInstallAsync(installed);
                OnlinePluginsStatus = $"Install of {entry.DisplayName} {version.Version} cancelled — unsigned plugin declined.";
                RefreshInstalledExtensions();
                return;
            }

            // Hot-load each newly-installed DLL through the plugin
            // loader. Packages typically have one plugin assembly + zero
            // or more transitive dep DLLs — TryLoadAsync rejects anything
            // that doesn't implement ITrayPlugin.
            var loadedCount = 0;
            foreach (var dllPath in installed)
            {
                var loaded = await _pluginLoader.LoadOneAsync(dllPath, CancellationToken.None);
                if (loaded is not null) loadedCount++;
            }

            OnlinePluginsStatus = loadedCount > 0
                ? $"Installed {entry.DisplayName} {version.Version} ({loadedCount} plugin(s) loaded)."
                : $"Installed {entry.DisplayName} {version.Version} but no plugin assembly was recognised — check the trust mode and logs.";

            RefreshInstalledExtensions();
            RefreshPluginConfigs();

            if (loadedCount > 0)
            {
                NotifyRequiredPermissionsForNewPlugin(entry);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to install plugin {PluginId} {Version}.", entry.Id, version.Version);
            OnlinePluginsStatus = $"Install failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenPluginsFolder()
    {
        try
        {
            _extensionInstaller.OpenPluginsFolder();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to open plugins folder.");
            ExtensionStatus = $"Could not open plugins folder: {ex.Message}";
        }
    }

    // Outcome of the install-time signature gate. Caller proceeds with
    // hot-load on Accept, deletes the installed files on Reject.
    private enum SignatureTrustDecision
    {
        Accept,
        Reject,
    }

    // Per-install signature gate. Runs after the bytes are written but
    // before LoadOneAsync. Returns Accept when the plugin's primary
    // assembly is acceptable under the configured TrustMode (either
    // signed-and-trusted, or unsigned with explicit user consent);
    // returns Reject when the user declines an unsigned install or when
    // RequireTrustedPublisher rejects a non-matching signature.
    //
    // completedChecks: human-readable list of vetting steps that actually
    // ran for THIS install. Shown verbatim in the prompt so the user can
    // make an informed call. Empty list means "nothing automated ran" —
    // surfaced honestly rather than glossed.
    private async Task<SignatureTrustDecision> EvaluateSignatureTrustAsync(
        string packageId,
        IReadOnlyList<string> installedDlls,
        string origin,
        IReadOnlyList<string> completedChecks)
    {
        // No DLLs to evaluate (extraction produced none): nothing to gate.
        if (installedDlls.Count == 0) return SignatureTrustDecision.Accept;

        // The primary plugin DLL is the one whose filename matches the
        // package id. Bundled transitive deps share the trust decision of
        // the primary — re-prompting per-DLL would be noise.
        var primary = installedDlls.FirstOrDefault(p =>
            string.Equals(Path.GetFileNameWithoutExtension(p), packageId, StringComparison.OrdinalIgnoreCase))
            ?? installedDlls[0];

        SignatureVerdict verdict;
        try
        {
            verdict = _signatureVerifier.Verify(primary);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Signature verification crashed for {Path}; treating as unsigned.", primary);
            verdict = SignatureVerdict.NotSigned;
        }

        switch (_pluginOptions.TrustMode)
        {
            case PluginTrustMode.RequireTrustedPublisher:
                if (verdict.IsSigned
                    && verdict.SignerThumbprint is { } thumb
                    && _pluginOptions.TrustedPublisherThumbprints.Contains(thumb, StringComparer.OrdinalIgnoreCase))
                {
                    return SignatureTrustDecision.Accept;
                }
                _logger.LogWarning(
                    "Plugin {PackageId} rejected by RequireTrustedPublisher: signed={IsSigned}, thumbprint={Thumbprint}.",
                    packageId, verdict.IsSigned, verdict.SignerThumbprint);
                _ = _notifier.ShowAsync(
                    new InformationRequest(
                        "Plugin blocked by policy",
                        $"{packageId} is not signed by a trusted publisher. Your administrator's plugin policy doesn't allow this install."),
                    CancellationToken.None);
                return SignatureTrustDecision.Reject;

            case PluginTrustMode.AllowUnsigned:
            case PluginTrustMode.RequireSigned:
            default:
                if (verdict.IsSigned) return SignatureTrustDecision.Accept;

                // Unsigned binary under the prompt model. Ask the user
                // once, surface what we already know about the package
                // (where it came from, exactly which checks ran), and
                // let them choose. Silence on the dispatcher side
                // defaults to Reject.
                var checksBlock = completedChecks.Count > 0
                    ? "Completed checks:\n" + string.Join("\n", completedChecks.Select(c => "  • " + c))
                    : "No automated checks ran for this install.";
                var prompt = new YesNoRequest(
                    Title: "Install unsigned plugin?",
                    Message:
                        $"{packageId} is not Authenticode-signed.\n\n" +
                        $"Source: {origin}\n\n" +
                        $"{checksBlock}\n\nInstall anyway?");
                var result = await _notifier.ShowAsync(prompt, CancellationToken.None).ConfigureAwait(true);
                if (result is YesNoResult { Accepted: true })
                {
                    _logger.LogInformation(
                        "User accepted unsigned plugin {PackageId} from {Origin}.", packageId, origin);
                    return SignatureTrustDecision.Accept;
                }
                _logger.LogInformation(
                    "User declined unsigned plugin {PackageId} from {Origin}.", packageId, origin);
                return SignatureTrustDecision.Reject;
        }
    }

    // Wipes the just-written DLLs after a declined / blocked install. We
    // call TryDeleteAsync per DLL because they may live in a per-plugin
    // subfolder; the installer detects that and removes the parent dir.
    private async Task CleanUpFailedInstallAsync(IReadOnlyList<string> installedDlls)
    {
        foreach (var path in installedDlls)
        {
            try
            {
                await _extensionInstaller.TryDeleteAsync(path, CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean up declined install at {Path}.", path);
            }
        }
    }

    private void RefreshInstalledExtensions()
    {
        InstalledExtensions.Clear();

        var loadedByFileName = _pluginLoader.LoadedPlugins
            .GroupBy(p => Path.GetFileName(p.AssemblyPath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var pending = _extensionInstaller.ListPendingUninstalls()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dllPath in _extensionInstaller.ListInstalledDlls())
        {
            var fileName = Path.GetFileName(dllPath);

            // Hide rows whose files are sentinel-marked for startup cleanup.
            // From the user's perspective the plugin is gone (already
            // unloaded); the lingering files are an internal janitorial
            // concern that gets resolved on next launch.
            if (pending.Contains(fileName)) continue;

            loadedByFileName.TryGetValue(fileName, out var loaded);

            InstalledExtensions.Add(new InstalledExtension(
                FileName: fileName,
                FullPath: dllPath,
                IsPendingUninstall: false,
                IsLoaded: loaded is not null,
                PluginId: loaded?.Plugin.Id,
                LoadedDisplayName: loaded?.Plugin.DisplayName,
                LoadedVersion: loaded?.Plugin.Version));
        }

        // The installed set just changed — re-apply the available-list
        // filter so the just-installed plugin disappears from the browse
        // list (and a just-uninstalled one reappears).
        RefreshAvailableList();
    }

    private bool CanCheckUpdates() => !IsBusy;

    private bool CanAddTenant() => !IsAddingTenant && !string.IsNullOrWhiteSpace(NewTenantId);

    private bool CanCancelAddTenant() => IsAddingTenant;

    private bool CanLookupDomain() => !IsLookingUpDomain && !string.IsNullOrWhiteSpace(NewTenantDomain);

    private bool CanSearchAppRegistrations()
        => !IsSearchingAppRegistrations && !string.IsNullOrWhiteSpace(NewTenantAppRegistrationName);

    private bool CanSignInWithWindows() => !IsAddingTenant;
}

public enum TenantAddMode
{
    Windows,
    Email,
    Manual,
}
