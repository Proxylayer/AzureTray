using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugin.LAPS;

// Windows LAPS plugin. Lists tenants' LAPS-managed devices and copies the
// rotating local-admin password to the clipboard on click.
public sealed class LapsPlugin : ITrayPlugin, IMenuChangeNotifier, IPluginConfigurable, IPluginTestProvider, IDisposable
{
    private const int DefaultMaxResultsPerMenu = 50;
    private const string MaxResultsOptionKey = "maxResultsPerMenu";

    private readonly ConcurrentDictionary<string, TenantDevices> _devicesByTenant
        = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private readonly Dictionary<string, object?> _optionValues = new(StringComparer.Ordinal)
    {
        [MaxResultsOptionKey] = DefaultMaxResultsPerMenu,
    };

    private IPluginContext? _context;
    private LapsGraphClient? _graph;
    private CancellationTokenSource? _lifetimeCts;

    public event Action? MenuChanged;
    public event Action? ValuesChanged;

    public IReadOnlyList<PluginOption> Options { get; } = new[]
    {
        new PluginOption(
            Key: MaxResultsOptionKey,
            Label: "Max devices per menu",
            Kind: PluginOptionKind.Number,
            Description: "Upper bound on devices shown before the user must refine the search.",
            DefaultValue: DefaultMaxResultsPerMenu),
    };

    public IReadOnlyDictionary<string, object?> Values => _optionValues;

    public void SetValue(string key, object? value)
    {
        if (!_optionValues.TryGetValue(key, out var current)) return;
        if (Equals(current, value)) return;
        _optionValues[key] = value;
        ValuesChanged?.Invoke();
        MenuChanged?.Invoke();
    }

    public object? BuildSettingsView() => null;

    private int MaxResultsPerMenu =>
        _optionValues.TryGetValue(MaxResultsOptionKey, out var v) && v is int n && n > 0
            ? n
            : DefaultMaxResultsPerMenu;

    public string Id => "net.proxylayer.AzureTray.Plugin.LAPS";

    public string DisplayName => "LAPS Passwords";

    public string Version { get; } = ResolveVersion();
    private static string ResolveVersion()
    {
        var asm = typeof(LapsPlugin).Assembly;
        var v = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrEmpty(v))
        {
            var plus = v.IndexOf('+', StringComparison.Ordinal);
            return plus >= 0 ? v[..plus] : v;
        }
        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    public int ApiVersion => PluginApiVersion.Current;

    public IReadOnlyList<PluginPermissionRequirement> RequiredPermissions { get; } = new[]
    {
        new PluginPermissionRequirement(
            Api: PermissionApi.MicrosoftGraph,
            ScopeName: "DeviceLocalCredential.Read.All",
            ScopeId: "280b3b69-0437-44b1-bc20-3b2fca1ee3e9",
            DisplayName: "Read Windows LAPS device credentials"),
    };

    public IReadOnlyList<PluginMenuItem> GetMenuItems()
    {
        TenantDevices[] tenants;
        lock (_stateLock)
        {
            tenants = _devicesByTenant.Values
                .OrderBy(t => t.Tenant.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        if (tenants.Length == 0)
        {
            return Array.Empty<PluginMenuItem>();
        }

        var anyLoading = tenants.Any(t =>
        {
            lock (t.Lock) return t.IsLoading;
        });

        return new[]
        {
            new PluginMenuItem(
                Text: "🔐  LAPS Passwords",
                IsBusy: anyLoading,
                Icon: anyLoading ? "↻" : null,
                SearchProvider: query => BuildDeviceRows(tenants, query),
                SearchPlaceholder: "Search devices…"),
        };
    }

    // Single flat row list across all tenants. Multi-tenant scenarios append
    // the tenant name in muted text to disambiguate same-named hosts; single-
    // tenant setups stay clean. Per-tenant loading and error states surface as
    // disabled rows inline so the user always sees that the plugin is alive.
    private IReadOnlyList<PluginMenuItem> BuildDeviceRows(TenantDevices[] tenants, string query)
    {
        var rows = new List<PluginMenuItem>();
        var matched = 0;
        var truncated = false;
        var hasQuery = !string.IsNullOrWhiteSpace(query);
        var multiTenant = tenants.Length > 1;

        foreach (var state in tenants)
        {
            IReadOnlyList<LapsDevice> source;
            bool isLoading;
            LoadStatus status;
            lock (state.Lock)
            {
                source = state.Devices;
                isLoading = state.IsLoading;
                status = state.Status;
            }

            // Show per-tenant status rows so a failing or loading tenant is
            // visible alongside successful ones in multi-tenant setups.
            var tenantHeader = multiTenant ? $"— {state.Tenant.DisplayName} —" : null;

            if (isLoading && source.Count == 0)
            {
                if (tenantHeader is not null) rows.Add(new PluginMenuItem(tenantHeader, IsEnabled: false));
                rows.Add(new PluginMenuItem("    (loading…)", IsEnabled: false));
                continue;
            }
            if (status == LoadStatus.AccessDenied)
            {
                if (tenantHeader is not null) rows.Add(new PluginMenuItem(tenantHeader, IsEnabled: false));
                rows.Add(new PluginMenuItem("    (no access — grant DeviceLocalCredential.Read.All)", IsEnabled: false));
                AddRetry(rows, state);
                continue;
            }
            if (status == LoadStatus.Failed)
            {
                if (tenantHeader is not null) rows.Add(new PluginMenuItem(tenantHeader, IsEnabled: false));
                rows.Add(new PluginMenuItem("    (failed to load — see logs)", IsEnabled: false));
                AddRetry(rows, state);
                continue;
            }

            var filtered = hasQuery
                ? source.Where(d => d.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList()
                : (IReadOnlyList<LapsDevice>)source;

            if (filtered.Count == 0)
            {
                // Suppress empty-result headers when the user is searching so
                // the menu doesn't fill up with "(no matches)" per tenant.
                if (hasQuery) continue;

                if (tenantHeader is not null) rows.Add(new PluginMenuItem(tenantHeader, IsEnabled: false));
                rows.Add(new PluginMenuItem("    (no LAPS devices)", IsEnabled: false));
                AddRetry(rows, state);
                continue;
            }

            if (tenantHeader is not null) rows.Add(new PluginMenuItem(tenantHeader, IsEnabled: false));

            foreach (var device in filtered)
            {
                if (matched >= MaxResultsPerMenu) { truncated = true; break; }
                var captured = device;
                var tenantId = state.Tenant.TenantId;
                rows.Add(new PluginMenuItem(
                    Text: multiTenant ? $"    {captured.DisplayName}" : captured.DisplayName,
                    Invoke: () => _ = CopyPasswordAsync(tenantId, captured)));
                matched++;
            }

            if (truncated) break;
        }

        if (rows.Count == 0)
        {
            return new[] { new PluginMenuItem("(no matches)", IsEnabled: false) };
        }

        if (truncated)
        {
            rows.Add(new PluginMenuItem(
                Text: $"(more results — refine search)",
                IsEnabled: false));
        }

        return rows;
    }

    private void AddRetry(List<PluginMenuItem> rows, TenantDevices state)
    {
        rows.Add(new PluginMenuItem(
            Text: "    Retry",
            Icon: "↻",
            KeepMenuOpen: true,
            Invoke: () => _ = LoadDevicesAsync(state)));
    }

    public Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken)
    {
        _context = context;
        _graph = new LapsGraphClient(context);
        _lifetimeCts = new CancellationTokenSource();

        context.TenantReady += OnTenantReady;
        context.TenantRemoved += OnTenantRemoved;

        // Load any tenants that became ready before this plugin loaded.
        foreach (var tenant in context.ReadyTenants)
        {
            OnTenantReady(tenant);
        }

        context.Logger.LogInformation(
            "LAPS plugin initialized; watching {ReadyCount} tenant(s).",
            context.ReadyTenants.Count);
        return Task.CompletedTask;
    }

    private void OnTenantReady(PluginTenant tenant)
    {
        var added = false;
        var state = _devicesByTenant.GetOrAdd(tenant.TenantId, _ =>
        {
            added = true;
            return new TenantDevices(tenant);
        });
        // Hydrate from disk on first sight of a tenant so the menu shows
        // last-known devices immediately, then poll Graph for fresh data.
        // Skip on re-fire (the in-memory state is already authoritative).
        if (added) LoadFromCache(state);
        _ = LoadDevicesAsync(state);
    }

    private void OnTenantRemoved(string tenantId)
    {
        _devicesByTenant.TryRemove(tenantId, out _);
        MenuChanged?.Invoke();
    }

    private async Task LoadDevicesAsync(TenantDevices state)
    {
        if (_graph is null || _context is null || _lifetimeCts is null) return;

        lock (state.Lock) state.IsLoading = true;
        MenuChanged?.Invoke();
        try
        {
            var devices = await _graph.ListDevicesAsync(state.Tenant.TenantId, _lifetimeCts.Token)
                .ConfigureAwait(false);

            lock (state.Lock)
            {
                state.Devices = devices;
                state.Status = LoadStatus.Loaded;
            }
            SaveToCache(state);
            _context.Logger.LogInformation(
                "LAPS loaded {Count} device(s) for tenant {TenantId} ({TenantName}).",
                devices.Count, state.Tenant.TenantId, state.Tenant.DisplayName);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            lock (state.Lock) state.Status = LoadStatus.AccessDenied;
            _context.Logger.LogInformation(
                "LAPS access denied for tenant {TenantId} ({TenantName}): {Status}. " +
                "Missing DeviceLocalCredential.Read.All consent or Cloud Device Administrator role.",
                state.Tenant.TenantId, state.Tenant.DisplayName, ex.StatusCode);
        }
        catch (Exception ex)
        {
            lock (state.Lock) state.Status = LoadStatus.Failed;
            _context.Logger.LogWarning(ex,
                "LAPS device fetch failed for tenant {TenantId} ({TenantName}).",
                state.Tenant.TenantId, state.Tenant.DisplayName);
        }
        finally
        {
            lock (state.Lock) state.IsLoading = false;
            MenuChanged?.Invoke();
        }
    }

    private async Task CopyPasswordAsync(string tenantId, LapsDevice device)
    {
        if (_graph is null || _context is null || _lifetimeCts is null) return;

        try
        {
            var password = await _graph.GetPasswordAsync(tenantId, device.DirectoryRecordId, _lifetimeCts.Token)
                .ConfigureAwait(false);

            if (string.IsNullOrEmpty(password))
            {
                _context.Logger.LogWarning(
                    "Graph returned no password for {DeviceName} (tenant {TenantId}).",
                    device.DisplayName, tenantId);
                await _context.Notifier.ShowAsync(
                    new InformationRequest(
                        Title: "LAPS",
                        Message: $"No password returned for {device.DisplayName}."),
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            _context.Clipboard.SetText(password);
            _context.Logger.LogInformation(
                "LAPS password for {DeviceName} ({TenantId}) copied to clipboard.",
                device.DisplayName, tenantId);

            await _context.Notifier.ShowAsync(
                new InformationRequest(
                    Title: "LAPS password copied",
                    Message: $"🔐 Password for {device.DisplayName} is on the clipboard."),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _context.Logger.LogError(ex,
                "Failed to retrieve LAPS password for {DeviceName} (tenant {TenantId}).",
                device.DisplayName, tenantId);
        }
    }

    // Tests exposed to the host's admin Test Runner.
    public IReadOnlyList<PluginTest> Tests => new[]
    {
        new PluginTest(
            "List devices for first ready tenant",
            "Calls Graph /directory/deviceLocalCredentials and reports the device count.",
            async ct =>
            {
                if (_graph is null || _context is null)
                    return PluginTestResult.Fail("Plugin not initialized.");

                var ready = _context.ReadyTenants;
                if (ready.Count == 0) return PluginTestResult.Fail("No tenants are ready.");
                var tenant = ready[0];

                try
                {
                    var devices = await _graph.ListDevicesAsync(tenant.TenantId, ct).ConfigureAwait(false);
                    return PluginTestResult.Pass($"Got {devices.Count} device(s) for {tenant.DisplayName}.");
                }
                catch (Exception ex)
                {
                    return PluginTestResult.Fail($"{ex.GetType().Name}: {ex.Message}");
                }
            }),
        new PluginTest(
            "Force-reload all tenants",
            "Triggers the LAPS device fetch for every tracked tenant.",
            async ct =>
            {
                if (_context is null) return PluginTestResult.Fail("Plugin not initialized.");

                TenantDevices[] tenants;
                lock (_stateLock) tenants = _devicesByTenant.Values.ToArray();
                if (tenants.Length == 0) return PluginTestResult.Fail("No tenants tracked.");

                await Task.WhenAll(tenants.Select(t => LoadDevicesAsync(t))).ConfigureAwait(false);
                return PluginTestResult.Pass($"Reloaded {tenants.Length} tenant(s).");
            }),
    };

    private string CachePath(string tenantId)
    {
        if (_context is null) return string.Empty;
        var safe = string.Join("_", tenantId.Split(System.IO.Path.GetInvalidFileNameChars()));
        return System.IO.Path.Combine(_context.DataDir, $"devices-{safe}.json");
    }

    private void LoadFromCache(TenantDevices state)
    {
        if (_context is null) return;
        var path = CachePath(state.Tenant.TenantId);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

        try
        {
            using var stream = System.IO.File.OpenRead(path);
            var cached = System.Text.Json.JsonSerializer.Deserialize<LapsDevice[]>(stream);
            if (cached is null || cached.Length == 0) return;

            lock (state.Lock)
            {
                state.Devices = cached;
                state.Status = LoadStatus.Loaded;
            }
            _context.Logger.LogInformation(
                "LAPS cache hydrated {Count} device(s) for tenant {TenantId}.",
                cached.Length, state.Tenant.TenantId);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex,
                "LAPS cache load failed for tenant {TenantId}; ignoring.",
                state.Tenant.TenantId);
        }
    }

    private void SaveToCache(TenantDevices state)
    {
        if (_context is null) return;
        var path = CachePath(state.Tenant.TenantId);
        if (string.IsNullOrEmpty(path)) return;

        try
        {
            System.IO.Directory.CreateDirectory(_context.DataDir);
            IReadOnlyList<LapsDevice> snapshot;
            lock (state.Lock) snapshot = state.Devices;
            using var stream = System.IO.File.Create(path);
            System.Text.Json.JsonSerializer.Serialize(stream, snapshot);
        }
        catch (Exception ex)
        {
            _context.Logger.LogWarning(ex,
                "LAPS cache save failed for tenant {TenantId}.",
                state.Tenant.TenantId);
        }
    }

    public Task ShutdownAsync(CancellationToken cancellationToken)
    {
        if (_context is not null)
        {
            _context.TenantReady -= OnTenantReady;
            _context.TenantRemoved -= OnTenantRemoved;
        }
        _lifetimeCts?.Cancel();
        _devicesByTenant.Clear();
        _graph = null;
        _context = null;
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _lifetimeCts?.Cancel();
        _lifetimeCts?.Dispose();
        _lifetimeCts = null;
    }

    // Per-tenant state cache. Devices list is mutated on Graph fetch
    // completion; we lock around reads/writes because the menu may build
    // from a UI thread while a load tick is finishing on a worker.
    internal sealed class TenantDevices
    {
        public TenantDevices(PluginTenant tenant) { Tenant = tenant; }
        public PluginTenant Tenant { get; }
        public object Lock { get; } = new();
        public IReadOnlyList<LapsDevice> Devices { get; set; } = Array.Empty<LapsDevice>();
        public LoadStatus Status { get; set; } = LoadStatus.NotLoaded;
        public bool IsLoading { get; set; }
    }

    internal enum LoadStatus
    {
        NotLoaded,
        Loaded,
        AccessDenied,
        Failed,
    }
}
