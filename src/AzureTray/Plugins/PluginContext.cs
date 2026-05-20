using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugins;

// Per-plugin IPluginContext. The host hands a fresh instance to each loaded
// plugin so per-tenant enable / disable can be filtered independently —
// the underlying ITenantReadinessTracker is shared, but each PluginContext
// only forwards events for tenants enabled for ITS plugin.
internal sealed class PluginContext : IPluginContext, IDisposable
{
    private readonly string _pluginId;
    private readonly ITenantReadinessTracker _readiness;
    private readonly IPluginConfigStore _configStore;
    private readonly IPluginHttpClientCore _httpCore;
    private readonly object _handlersLock = new();
    private readonly List<Action<PluginTenant>> _readyHandlers = new();
    private readonly List<Action<string>> _removedHandlers = new();

    private bool _disposed;

    public PluginContext(
        string pluginId,
        ILogger logger,
        IPluginHttpClientCore httpCore,
        INotifier notifier,
        IClipboard clipboard,
        IReadOnlyList<PluginTenant> tenants,
        ITenantReadinessTracker readiness,
        IPluginConfigStore configStore,
        string graphScope,
        string armScope,
        string dataDir,
        string? hostVersion)
    {
        // Assign the readiness tracker and config store BEFORE filtering
        // Tenants — IsEnabled() reads _configStore so the order matters.
        _pluginId = pluginId;
        _readiness = readiness;
        _configStore = configStore;
        Logger = logger;
        _httpCore = httpCore;
        Notifier = notifier;
        Clipboard = clipboard;
        Tenants = tenants.Where(t => IsEnabled(t.TenantId)).ToArray();
        GraphScope = graphScope;
        ArmScope = armScope;
        DataDir = dataDir;
        HostVersion = hostVersion;

        _readiness.TenantReady += OnTenantReadyFromTracker;
        _readiness.TenantRemoved += OnTenantRemovedFromTracker;
        _configStore.PluginConfigChanged += OnPluginConfigChanged;
    }

    public ILogger Logger { get; }
    public INotifier Notifier { get; }
    public IClipboard Clipboard { get; }
    public IReadOnlyList<PluginTenant> Tenants { get; private set; }
    public string GraphScope { get; }
    public string ArmScope { get; }
    public string DataDir { get; }
    public string? HostVersion { get; }

    public IPluginHttpClient GetHttpClient(string tenantId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        if (!IsEnabled(tenantId))
        {
            throw new ArgumentException(
                $"Tenant '{tenantId}' is not enabled for plugin '{_pluginId}'.",
                nameof(tenantId));
        }
        return new TenantScopedPluginHttpClient(_httpCore, tenantId);
    }

    public IReadOnlyList<PluginTenant> ReadyTenants =>
        _readiness.ReadyTenants.Where(t => IsEnabled(t.TenantId)).ToArray();

    public bool IsTenantReady(string tenantId) =>
        IsEnabled(tenantId) && _readiness.IsReady(tenantId);

    public event Action<PluginTenant> TenantReady
    {
        add { lock (_handlersLock) _readyHandlers.Add(value); }
        remove { lock (_handlersLock) _readyHandlers.Remove(value); }
    }

    public event Action<string> TenantRemoved
    {
        add { lock (_handlersLock) _removedHandlers.Add(value); }
        remove { lock (_handlersLock) _removedHandlers.Remove(value); }
    }

    private bool IsEnabled(string tenantId) => _configStore.IsTenantEnabledFor(_pluginId, tenantId);

    private void OnTenantReadyFromTracker(PluginTenant tenant)
    {
        if (!IsEnabled(tenant.TenantId)) return;
        InvokeReady(tenant);
    }

    private void OnTenantRemovedFromTracker(string tenantId) => InvokeRemoved(tenantId);

    // The user toggling a tenant on/off for this plugin needs to materialize
    // as the same TenantReady / TenantRemoved events the plugin already
    // subscribes to — otherwise the plugin can't react until restart.
    private void OnPluginConfigChanged(string pluginId)
    {
        if (!string.Equals(pluginId, _pluginId, StringComparison.OrdinalIgnoreCase)) return;

        var ready = _readiness.ReadyTenants;
        foreach (var tenant in ready)
        {
            var enabled = IsEnabled(tenant.TenantId);
            // Heuristic: re-fire TenantReady for newly enabled tenants;
            // re-fire TenantRemoved for newly disabled tenants. Plugins that
            // are idempotent on these events (PIM, LAPS) handle the
            // duplicates cleanly via their internal "already tracking" check.
            if (enabled) InvokeReady(tenant);
            else InvokeRemoved(tenant.TenantId);
        }
    }

    private void InvokeReady(PluginTenant tenant)
    {
        Action<PluginTenant>[] handlers;
        lock (_handlersLock) handlers = _readyHandlers.ToArray();
        foreach (var h in handlers)
        {
            try { h(tenant); } catch { /* plugin owns its handlers */ }
        }
    }

    private void InvokeRemoved(string tenantId)
    {
        Action<string>[] handlers;
        lock (_handlersLock) handlers = _removedHandlers.ToArray();
        foreach (var h in handlers)
        {
            try { h(tenantId); } catch { /* plugin owns its handlers */ }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _readiness.TenantReady -= OnTenantReadyFromTracker;
        _readiness.TenantRemoved -= OnTenantRemovedFromTracker;
        _configStore.PluginConfigChanged -= OnPluginConfigChanged;
        lock (_handlersLock)
        {
            _readyHandlers.Clear();
            _removedHandlers.Clear();
        }
    }
}
