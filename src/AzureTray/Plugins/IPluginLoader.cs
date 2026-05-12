using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugins;

public interface IPluginLoader
{
    IReadOnlyList<LoadedPlugin> LoadedPlugins { get; }

    Task LoadAllAsync(CancellationToken cancellationToken);

    Task UnloadAllAsync(CancellationToken cancellationToken);

    // Load a single DLL into the host without an app restart. Idempotent: if
    // the DLL is already loaded the call is a no-op. Returns the LoadedPlugin
    // on success, or null if signature / API version / construction rejected it.
    Task<LoadedPlugin?> LoadOneAsync(string dllPath, CancellationToken cancellationToken);

    // Unload a single plugin (by id) so its DLL can be deleted or replaced.
    // Returns true if the plugin was found and shut down.
    Task<bool> UnloadOneAsync(string pluginId, CancellationToken cancellationToken);

    // Raised after LoadedPlugins changes (load / unload). Consumers refresh
    // any view of the loaded set — TrayIcon re-wires menu-change subscriptions,
    // Settings refreshes the per-plugin config UI.
    event Action? PluginsChanged;
}
