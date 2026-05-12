using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Plugins;

// Source of truth for "this tenant is ready — its credential acquired a token".
// Host-side singleton; PluginContext forwards subscriptions through it so every
// loaded plugin sees the same lifecycle signals.
//
// "Ready" means a successful Azure.Identity GetTokenAsync round-trip — the
// middle of the three definitions discussed when designing this:
//   * Not just "tenant is in the store" (catches misconfigured ClientId).
//   * Not as expensive as a Graph /me probe (that's the AddTenant flow's job).
public interface ITenantReadinessTracker
{
    event Action<PluginTenant> TenantReady;
    event Action<string> TenantRemoved;

    IReadOnlyList<PluginTenant> ReadyTenants { get; }

    bool IsReady(string tenantId);

    // Idempotent. Re-marking the same tenant does not re-fire TenantReady.
    void MarkReady(PluginTenant tenant);

    // Idempotent. Fires TenantRemoved only if the tenant was previously ready.
    void MarkRemoved(string tenantId);
}

public sealed class TenantReadinessTracker : ITenantReadinessTracker
{
    private readonly ConcurrentDictionary<string, PluginTenant> _ready
        = new(StringComparer.OrdinalIgnoreCase);

    public event Action<PluginTenant>? TenantReady;
    public event Action<string>? TenantRemoved;

    public IReadOnlyList<PluginTenant> ReadyTenants => _ready.Values.ToArray();

    public bool IsReady(string tenantId)
        => !string.IsNullOrWhiteSpace(tenantId) && _ready.ContainsKey(tenantId);

    public void MarkReady(PluginTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentException.ThrowIfNullOrWhiteSpace(tenant.TenantId);

        // TryAdd guarantees we only fire once per tenant. If the display name
        // changed, the existing entry stays — plugins keyed by tenantId still
        // work, and the rename will surface on next restart.
        if (_ready.TryAdd(tenant.TenantId, tenant))
        {
            TenantReady?.Invoke(tenant);
        }
    }

    public void MarkRemoved(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        if (_ready.TryRemove(tenantId, out _))
        {
            TenantRemoved?.Invoke(tenantId);
        }
    }
}
