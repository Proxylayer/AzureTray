using System;
using System.Collections.Generic;

namespace AzureTray.Plugins;

// Host-side persistence for per-plugin enable + option values. Plugins do
// not see this directly — the host applies the enable set when fanning out
// TenantReady events and feeds option values through IPluginConfigurable.
public interface IPluginConfigStore
{
    bool IsTenantEnabledFor(string pluginId, string tenantId);

    IReadOnlySet<string> GetDisabledTenants(string pluginId);

    void SetTenantEnabled(string pluginId, string tenantId, bool enabled);

    IReadOnlyDictionary<string, object?> GetOptions(string pluginId);

    void SetOption(string pluginId, string key, object? value);

    // Raised after a successful write so the host (PluginContext + UI) can
    // react. Single-arg: the plugin id whose config changed.
    event Action<string>? PluginConfigChanged;
}
