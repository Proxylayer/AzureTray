using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

// Top-level plugin contract. Implementations must have a public parameterless
// constructor — the host activates the type directly.
public interface ITrayPlugin
{
    // Stable identifier for the plugin, used in logs and config keys.
    // Convention: reverse-DNS, e.g. "com.example.MyPlugin".
    string Id { get; }

    // User-visible name shown in the host's plugin list.
    string DisplayName { get; }

    // Semantic version of the plugin (NOT the contract API version). Free-form.
    string Version { get; }

    // The PluginApiVersion.Current the plugin was built against. The host rejects
    // plugins whose ApiVersion does not equal its own PluginApiVersion.Current.
    int ApiVersion { get; }

    // Delegated OAuth scopes the plugin requires the host's app registration to
    // expose (and admin-consent) in every managed tenant. The host aggregates
    // requirements across plugins, then offers a unified "Fix Permissions"
    // action that adds the missing scopes and grants admin consent.
    //
    // Default is empty so most plugins don't need to override.
    IReadOnlyList<PluginPermissionRequirement> RequiredPermissions
        => Array.Empty<PluginPermissionRequirement>();

    // Items this plugin contributes to the tray context menu. Called each time
    // the menu is rebuilt (right-click on the tray icon). Plugins may return
    // fresh state — the host does not cache. Keep this fast; it runs on the UI
    // thread. Default is empty so plugins that don't need a menu surface can
    // ignore this.
    IReadOnlyList<PluginMenuItem> GetMenuItems()
        => Array.Empty<PluginMenuItem>();

    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    Task ShutdownAsync(CancellationToken cancellationToken);
}
