using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AzureTray.AppRegistration;
using AzureTray.Configuration;
using AzureTray.Models;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;
using AzureTray.Tenants;

namespace AzureTray.Testing;

// Single source of truth for runnable tests. Built-in tests are wired here;
// plugin tests pulled from each LoadedPlugin that implements IPluginTestProvider.
public sealed class TestRegistry : ITestRegistry
{
    private readonly IPluginLoader _pluginLoader;
    private readonly INotifier _notifier;
    private readonly IClipboard _clipboard;
    private readonly ITenantStore _tenantStore;
    private readonly IAppRegistrationDiscovery _appRegistrationDiscovery;
    private readonly IAppRegistrationPermissions _appRegistrationPermissions;
    private readonly IAppRegistrationProvisioning _appRegistrationProvisioning;
    private readonly AuthOptions _authOptions;
    private readonly ILogger<TestRegistry> _logger;

    public TestRegistry(
        IPluginLoader pluginLoader,
        INotifier notifier,
        IClipboard clipboard,
        ITenantStore tenantStore,
        IAppRegistrationDiscovery appRegistrationDiscovery,
        IAppRegistrationPermissions appRegistrationPermissions,
        IAppRegistrationProvisioning appRegistrationProvisioning,
        IOptions<AuthOptions> authOptions,
        ILogger<TestRegistry> logger)
    {
        _pluginLoader = pluginLoader;
        _notifier = notifier;
        _clipboard = clipboard;
        _tenantStore = tenantStore;
        _appRegistrationDiscovery = appRegistrationDiscovery;
        _appRegistrationPermissions = appRegistrationPermissions;
        _appRegistrationProvisioning = appRegistrationProvisioning;
        _authOptions = authOptions.Value;
        _logger = logger;
    }

    public IReadOnlyList<TestGroup> GetGroups()
    {
        var groups = new List<TestGroup>
        {
            new("Host: Notifications", BuildNotificationTests()),
            new("Host: Clipboard", BuildClipboardTests()),
            new("Host: Logger", BuildLoggerTests()),
            new("Host: App Registration", BuildAppRegistrationTests()),
        };

        foreach (var loaded in _pluginLoader.LoadedPlugins)
        {
            if (loaded.Plugin is not IPluginTestProvider provider) continue;
            IReadOnlyList<PluginTest> tests;
            try { tests = provider.Tests; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {Id} threw from Tests getter; skipping.", loaded.Plugin.Id);
                continue;
            }
            if (tests.Count == 0) continue;

            groups.Add(new TestGroup($"Plugin: {loaded.Plugin.DisplayName}", tests));
        }

        return groups;
    }

    // ─── Built-in test definitions ────────────────────────────────────────

    private static readonly string[] ChoicePromptOptions = { "Alpha", "Beta", "Gamma" };

    private PluginTest[] BuildNotificationTests() => new[]
    {
        new PluginTest(
            "Information toast",
            "Shows a passive toast that auto-dismisses after a few seconds.",
            async ct =>
            {
                await _notifier.ShowAsync(new InformationRequest(
                    "Test toast",
                    "If you see this, passive notifications work."), ct);
                return PluginTestResult.Pass("Toast displayed.");
            }),
        new PluginTest(
            "Yes/No prompt",
            "Shows a Yes/No dialog and reports which button you clicked.",
            async ct =>
            {
                var result = await _notifier.ShowAsync(new YesNoRequest(
                    "Test prompt",
                    "Pick either button to confirm interactive notifications work."), ct);
                return result switch
                {
                    YesNoResult yn => PluginTestResult.Pass($"Got {(yn.Accepted ? "Yes" : "No")}."),
                    DismissedResult => PluginTestResult.Fail("Dismissed without a choice."),
                    _ => PluginTestResult.Fail($"Unexpected result type: {result.GetType().Name}"),
                };
            }),
        new PluginTest(
            "Choice prompt",
            "Shows a choice list with an 'Other' free-text option.",
            async ct =>
            {
                var result = await _notifier.ShowAsync(new ChoiceRequest(
                    "Test choice",
                    "Pick anything (or type something into Other).",
                    ChoicePromptOptions,
                    AllowOther: true), ct);
                return result switch
                {
                    ChoiceResult cr => PluginTestResult.Pass(cr.SelectedChoice is not null
                        ? $"Selected: {cr.SelectedChoice}"
                        : $"Other: {cr.OtherText}"),
                    DismissedResult => PluginTestResult.Fail("Dismissed."),
                    _ => PluginTestResult.Fail($"Unexpected: {result.GetType().Name}"),
                };
            }),
        new PluginTest(
            "Text input prompt",
            "Asks for a string. Pass = you submit any non-empty value.",
            async ct =>
            {
                var result = await _notifier.ShowAsync(new TextInputRequest(
                    "Test input",
                    "Type anything and submit.",
                    Placeholder: "your text here"), ct);
                return result switch
                {
                    TextInputResult tr when !string.IsNullOrWhiteSpace(tr.Text) =>
                        PluginTestResult.Pass($"Got: {tr.Text}"),
                    TextInputResult => PluginTestResult.Fail("Empty submission."),
                    DismissedResult => PluginTestResult.Fail("Dismissed."),
                    _ => PluginTestResult.Fail($"Unexpected: {result.GetType().Name}"),
                };
            }),
    };

    private PluginTest[] BuildClipboardTests() => new[]
    {
        new PluginTest(
            "Round-trip text",
            "Writes a sentinel value to the clipboard. Paste somewhere to confirm.",
            ct =>
            {
                var sentinel = $"AzureTray clipboard test @ {DateTime.Now:HH:mm:ss}";
                _clipboard.SetText(sentinel);
                return Task.FromResult(PluginTestResult.Pass($"Wrote: {sentinel}"));
            }),
    };

    private PluginTest[] BuildLoggerTests() => new[]
    {
        new PluginTest(
            "Emit one of each level",
            "Writes Trace/Debug/Information/Warning/Error/Critical entries.",
            ct =>
            {
                _logger.LogTrace("Test runner: trace");
                _logger.LogDebug("Test runner: debug");
                _logger.LogInformation("Test runner: information");
                _logger.LogWarning("Test runner: warning");
                _logger.LogError("Test runner: error");
                _logger.LogCritical("Test runner: critical");
                return Task.FromResult(PluginTestResult.Pass("Open the Log Viewer to verify each level was emitted."));
            }),
    };

    // ─── App-registration end-to-end tests ────────────────────────────────
    //
    // These exercise the live Microsoft Graph plumbing against the user's
    // actual tenant(s) rather than the mocked HTTP used in the unit-test
    // project. Read-only tests are safe to run repeatedly; the destructive
    // ones (Fix Permissions, Create App Registration) gate on an explicit
    // Yes/No confirmation before touching anything.

    private PluginTest[] BuildAppRegistrationTests() => new[]
    {
        new PluginTest(
            "Discover by display name (read-only)",
            "Calls /v1.0/applications?$filter=displayName eq … on the selected tenant using the configured AppRegistrationName. Verifies the Graph token + discovery wire path.",
            async ct =>
            {
                var tenant = await PickTenantAsync(allowNoClientId: true, ct);
                if (tenant is null) return PluginTestResult.Fail("No tenant selected.");

                var name = _authOptions.AppRegistrationName;
                if (string.IsNullOrWhiteSpace(name))
                    return PluginTestResult.Fail("AppRegistrationName is empty in appsettings.json.");

                var info = await _appRegistrationDiscovery.FindByDisplayNameAsync(tenant.TenantId, name, ct);
                return info is null
                    ? PluginTestResult.Fail($"No app registration named '{name}' in tenant '{tenant.DisplayName}'.")
                    : PluginTestResult.Pass($"Found '{info.DisplayName}' (appId {info.AppId}).");
            }),

        new PluginTest(
            "Check permissions on current app registration (read-only)",
            "Aggregates host + plugin scope requirements and calls CheckAsync against the tenant's dedicated app registration. Reports the missing/unconsented counts.",
            async ct =>
            {
                var tenant = await PickTenantAsync(allowNoClientId: false, ct);
                if (tenant is null) return PluginTestResult.Fail("No tenant with a dedicated app registration was selected.");

                var required = AggregateRequiredPermissions();
                if (required.Count == 0) return PluginTestResult.Pass("No required permissions to check.");

                var result = await _appRegistrationPermissions.CheckAsync(tenant.TenantId, tenant.ClientId!, required, ct);
                if (result.IsFullyConfigured)
                {
                    return PluginTestResult.Pass($"Tenant '{tenant.DisplayName}' is fully configured ({required.Count} scope(s) required).");
                }
                return PluginTestResult.Fail(
                    $"Tenant '{tenant.DisplayName}': {result.Missing.Count} scope(s) missing from manifest, {result.NotConsented.Count} not admin-consented.");
            }),

        new PluginTest(
            "Ensure WAM broker redirect URI (idempotent)",
            "Patches publicClient.redirectUris to include 'ms-appx-web://microsoft.aad.brokerplugin/{appId}'. Safe to re-run; no-op if already present.",
            async ct =>
            {
                var tenant = await PickTenantAsync(allowNoClientId: false, ct);
                if (tenant is null) return PluginTestResult.Fail("No tenant with a dedicated app registration was selected.");

                var added = await _appRegistrationProvisioning.EnsureBrokerRedirectUriAsync(tenant.TenantId, tenant.ClientId!, ct);
                return added
                    ? PluginTestResult.Pass($"Broker redirect URI present on app {tenant.ClientId}.")
                    : PluginTestResult.Fail($"App {tenant.ClientId} not found in tenant '{tenant.DisplayName}'.");
            }),

        new PluginTest(
            "Fix Permissions (DESTRUCTIVE — replaces scopes)",
            "Calls EnsureAsync with replace semantics — stale scopes get pruned from requiredResourceAccess and from oauth2PermissionGrants. Requires Global Administrator on the selected tenant.",
            async ct =>
            {
                if (!await ConfirmDestructiveAsync(
                    "Fix Permissions test",
                    "This will rewrite the tenant's app-registration scopes to exactly the host + plugin set, removing any stale scopes. Proceed?",
                    ct)) return PluginTestResult.Fail("Cancelled.");

                var tenant = await PickTenantAsync(allowNoClientId: false, ct);
                if (tenant is null) return PluginTestResult.Fail("No tenant with a dedicated app registration was selected.");

                var required = AggregateRequiredPermissions();
                if (required.Count == 0) return PluginTestResult.Fail("No required permissions to apply.");

                var result = await _appRegistrationPermissions.EnsureAsync(tenant.TenantId, tenant.ClientId!, required, ct);
                return PluginTestResult.Pass(
                    $"Tenant '{tenant.DisplayName}': added {result.ScopesAdded.Count} scope(s), removed {result.StaleScopesRemoved} stale; granted {result.GrantsAdded.Count}, removed {result.StaleGrantsRemoved} stale grant(s).");
            }),

        new PluginTest(
            "Create App Registration (DESTRUCTIVE — creates a real app)",
            "Provisions a brand-new app registration end-to-end in a tenant that has no dedicated one yet, persists the new clientId on the tenant, and admin-consents the host + plugin scopes. Requires Application Administrator.",
            async ct =>
            {
                if (!await ConfirmDestructiveAsync(
                    "Create App Registration test",
                    $"This will POST a new application named '{_authOptions.AppRegistrationName}' (plus servicePrincipal and consent grants) in the selected tenant. Proceed?",
                    ct)) return PluginTestResult.Fail("Cancelled.");

                var tenant = await PickTenantAsync(allowNoClientId: true, requireNoClientId: true, ct);
                if (tenant is null) return PluginTestResult.Fail("No eligible tenant (must currently lack a dedicated app registration).");

                var name = _authOptions.AppRegistrationName;
                if (string.IsNullOrWhiteSpace(name))
                    return PluginTestResult.Fail("AppRegistrationName is empty in appsettings.json.");

                var required = AggregateRequiredPermissions();
                var result = await _appRegistrationProvisioning.CreateAsync(tenant.TenantId, name, required, ct);

                // Persist the new clientId on the tenant so subsequent sign-ins
                // use the dedicated app reg, matching the Settings UI flow.
                var updated = new Tenant(tenant.TenantId, tenant.DisplayName, result.App.AppId);
                await _tenantStore.AddOrUpdateAsync(updated, ct);

                var brokerNote = result.BrokerRedirectUriAdded ? "" : " (WAM broker URI NOT added — re-run Ensure to fix)";
                return PluginTestResult.Pass(
                    $"Created '{name}' (appId {result.App.AppId}) in '{tenant.DisplayName}', consented {result.ScopesGranted} scope(s){brokerNote}.");
            }),
    };

    private async Task<Tenant?> PickTenantAsync(bool allowNoClientId, CancellationToken ct)
        => await PickTenantAsync(allowNoClientId, requireNoClientId: false, ct);

    private async Task<Tenant?> PickTenantAsync(bool allowNoClientId, bool requireNoClientId, CancellationToken ct)
    {
        var candidates = _tenantStore.GetAll()
            .Where(t => requireNoClientId
                ? string.IsNullOrWhiteSpace(t.ClientId)
                : (allowNoClientId || !string.IsNullOrWhiteSpace(t.ClientId)))
            .ToList();

        if (candidates.Count == 0) return null;

        // Single eligible tenant — skip the picker.
        if (candidates.Count == 1) return candidates[0];

        var labels = candidates.Select(t => $"{t.DisplayName}  —  {t.TenantId}").ToList();
        var result = await _notifier.ShowAsync(new ChoiceRequest(
            "Select tenant",
            "Which tenant should this test target?",
            labels), ct);

        if (result is not ChoiceResult cr || cr.SelectedChoice is null) return null;
        var idx = labels.IndexOf(cr.SelectedChoice);
        return idx >= 0 ? candidates[idx] : null;
    }

    private async Task<bool> ConfirmDestructiveAsync(string title, string message, CancellationToken ct)
    {
        var result = await _notifier.ShowAsync(new YesNoRequest(title, message), ct);
        return result is YesNoResult yn && yn.Accepted;
    }

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
}
