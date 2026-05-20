using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Top-level plugin contract. Implementations must have a public parameterless
/// constructor — the host activates the type directly via reflection.
/// </summary>
public interface ITrayPlugin
{
    /// <summary>
    /// Stable reverse-DNS identifier, used in logs, config keys, and the plugin
    /// registry (e.g. <c>"com.example.MyPlugin"</c>). Must be unique.
    /// </summary>
    string Id { get; }

    /// <summary>User-visible name shown in the host's plugin list.</summary>
    string DisplayName { get; }

    /// <summary>
    /// Semantic version of the plugin itself (not the contract API version).
    /// Displayed alongside <see cref="DisplayName"/> in the plugin list.
    /// </summary>
    string Version { get; }

    /// <summary>
    /// The <see cref="PluginApiVersion.Current"/> value the plugin was compiled
    /// against. The host rejects any plugin whose declared value does not match
    /// its own — keeps ABI mismatches loud and visible.
    /// </summary>
    int ApiVersion { get; }

    /// <summary>
    /// Delegated OAuth scopes the plugin needs the host's app registration to
    /// expose and admin-consent in every managed tenant. The host aggregates
    /// requirements across all loaded plugins and offers a unified
    /// "Fix Permissions" action.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Declaring scopes here lets the host pre-consent before the first HTTP
    /// call, preventing silent 401 failures on first use.
    /// </para>
    /// <para>
    /// <strong>Security:</strong> only declare scopes you actually use.
    /// Over-privileged declarations are flagged by the host's permission auditor
    /// and violate the principle of least privilege.
    /// </para>
    /// </remarks>
    IReadOnlyList<PluginPermissionRequirement> RequiredPermissions
        => Array.Empty<PluginPermissionRequirement>();

    /// <summary>
    /// Items this plugin contributes to the tray context menu. Called on every
    /// right-click; the host does not cache results. Return fresh state freely.
    /// </summary>
    /// <remarks>
    /// This method runs on the <strong>UI thread</strong> — keep it fast and
    /// synchronous. Start background work in <see cref="InitializeAsync"/> and
    /// fire <see cref="IMenuChangeNotifier.MenuChanged"/> when data arrives.
    /// </remarks>
    IReadOnlyList<PluginMenuItem> GetMenuItems()
        => Array.Empty<PluginMenuItem>();

    /// <summary>
    /// Called once after the plugin is loaded. Start background pollers,
    /// subscribe to <see cref="IPluginContext.TenantReady"/>, and restore
    /// cached state from <see cref="IPluginContext.DataDir"/> here.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Iterate <see cref="IPluginContext.ReadyTenants"/> inside this method
    /// to handle tenants that became ready before the plugin loaded.
    /// </para>
    /// <para>
    /// <strong>Security:</strong> do not make outbound API calls until at least
    /// one tenant is ready (check <see cref="IPluginContext.IsTenantReady"/>).
    /// </para>
    /// </remarks>
    Task InitializeAsync(IPluginContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Called when the host is shutting down or the plugin is being unloaded.
    /// Cancel background work, flush pending state to
    /// <see cref="IPluginContext.DataDir"/>, and <strong>unsubscribe from all
    /// <see cref="IPluginContext"/> events</strong> to prevent memory leaks
    /// when the host reloads the plugin into a fresh load context.
    /// </summary>
    Task ShutdownAsync(CancellationToken cancellationToken);
}
