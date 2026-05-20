using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Services the host hands to each plugin at initialisation. Every property
/// is stable surface across a major <see cref="PluginApiVersion"/> version;
/// adding members requires either default-implemented interface methods or an
/// <see cref="PluginApiVersion"/> bump.
/// </summary>
public interface IPluginContext
{
    /// <summary>Structured logger scoped to this plugin's assembly name.</summary>
    ILogger Logger { get; }

    /// <summary>
    /// Authenticated HTTP egress. The host owns the named clients and credentials.
    /// See <see cref="IPluginHttpClient"/> and <see cref="PluginHttpClientNames"/>.
    /// </summary>
    IPluginHttpClient Http { get; }

    /// <summary>
    /// Interactive notifications surfaced near the tray icon.
    /// See <see cref="INotifier"/> for request types and security guidance.
    /// </summary>
    INotifier Notifier { get; }

    /// <summary>
    /// System clipboard adapter. Use for "Copy" actions without referencing
    /// WPF/WinForms types. See the security note on <see cref="IClipboard"/>.
    /// </summary>
    IClipboard Clipboard { get; }

    /// <summary>
    /// Read-only snapshot of all tenants the user has configured in the host.
    /// Use <see cref="TenantReady"/>/<see cref="TenantRemoved"/> to react to
    /// changes after <see cref="ITrayPlugin.InitializeAsync"/>.
    /// </summary>
    IReadOnlyList<PluginTenant> Tenants { get; }

    /// <summary>
    /// Tenants for which the host has confirmed auth works (a token was
    /// successfully acquired). Plugins <strong>must not</strong> call
    /// <see cref="IPluginHttpClient.SendAsync"/> against a tenant until it
    /// appears here or a <see cref="TenantReady"/> event fires.
    /// </summary>
    /// <remarks>
    /// Iterate this in <see cref="ITrayPlugin.InitializeAsync"/> to backfill
    /// tenants that became ready before the plugin was loaded.
    /// </remarks>
    IReadOnlyList<PluginTenant> ReadyTenants { get; }

    /// <summary>Returns <c>true</c> if <paramref name="tenantId"/> is in <see cref="ReadyTenants"/>.</summary>
    bool IsTenantReady(string tenantId);

    /// <summary>
    /// Fired when a tenant transitions to ready (token acquired successfully).
    /// May be invoked on a thread-pool thread — marshal to the UI thread if needed.
    /// </summary>
    /// <remarks>
    /// Always unsubscribe in <see cref="ITrayPlugin.ShutdownAsync"/> to prevent
    /// memory leaks when the host reloads the plugin.
    /// </remarks>
    event Action<PluginTenant> TenantReady;

    /// <summary>
    /// Fired when a tenant is removed from the host's configuration or its
    /// token is invalidated. Plugins should pause outbound calls for that
    /// tenant until a new <see cref="TenantReady"/> event arrives.
    /// </summary>
    event Action<string> TenantRemoved;

    /// <summary>
    /// OAuth scope string for Microsoft Graph, resolved from the host's cloud
    /// configuration. Use with <see cref="PluginHttpClientNames.Graph"/> and
    /// <see cref="IPluginHttpClient.SendAsync"/> so calls work in sovereign
    /// clouds as well as public Azure.
    /// </summary>
    string GraphScope { get; }

    /// <summary>
    /// OAuth scope string for Azure Resource Manager, resolved from the host's
    /// cloud configuration. Use with <see cref="PluginHttpClientNames.Arm"/>.
    /// </summary>
    string ArmScope { get; }

    /// <summary>
    /// Host-managed per-plugin scratch directory. Freely read/write here —
    /// typical use is caching last-known state so a restart doesn't show a
    /// blank menu while the first poll runs. The host creates the directory
    /// before passing it; the path survives across plugin reloads.
    /// </summary>
    /// <remarks>
    /// <strong>Security:</strong> always join paths with
    /// <c>Path.Combine</c> and never accept path segments from untrusted
    /// input, to prevent path-traversal out of the plugin's data directory.
    /// </remarks>
    string DataDir { get; }

    /// <summary>
    /// SemVer string of the running host (e.g. <c>"0.3.0"</c>). Use to
    /// conditionally enable features that depend on host capabilities
    /// introduced after the plugin's <see cref="PluginApiVersion"/> baseline.
    /// Returns <c>null</c> on older hosts that don't supply it — fall back
    /// to the safe minimum behaviour when <c>null</c>.
    /// </summary>
    string? HostVersion => null;
}
