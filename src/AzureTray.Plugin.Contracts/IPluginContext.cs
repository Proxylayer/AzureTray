using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    /// Returns a tenant-scoped HTTP client for the given tenant. The client's
    /// <see cref="IPluginHttpClient.SendAsync"/> will only ever acquire tokens
    /// for <paramref name="tenantId"/> — the tenant is baked in at acquisition
    /// time and cannot be overridden per-call.
    /// </summary>
    /// <remarks>
    /// Call this once per tenant (or cache the result) and hold the reference
    /// for the lifetime of your per-tenant component. Prefer obtaining the
    /// client in <see cref="ITrayPlugin.InitializeAsync"/> or when handling a
    /// <see cref="TenantReady"/> event rather than on every request.
    /// </remarks>
    /// <param name="tenantId">
    /// The tenant to scope this client to. Must correspond to a tenant enabled
    /// for this plugin; the host throws <see cref="ArgumentException"/> otherwise.
    /// </param>
    IPluginHttpClient GetHttpClient(string tenantId);

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

    /// <summary>
    /// Forces the host to re-acquire this tenant's access tokens, bypassing the
    /// cached ones, so claims that changed service-side (for example new role
    /// memberships after a PIM activation was approved) take effect on the next
    /// <see cref="IPluginHttpClient.SendAsync"/> instead of when the cached
    /// token would have expired.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Safe to call repeatedly and from multiple threads; the host serializes
    /// and debounces refreshes per tenant. Never triggers interactive sign-in:
    /// if the silent path cannot refresh, the host logs it and reports failure
    /// rather than popping a broker window. Nothing is thrown at the plugin.
    /// </para>
    /// <para>
    /// Returns <c>false</c> when the host does not implement this member (any
    /// host older than the contract version that introduced it), when the
    /// tenant is not enabled for this plugin, or when no genuinely new token
    /// could be obtained. Treat <c>false</c> as "carry on, the change will
    /// surface when the token naturally rolls over" — not as an error.
    /// </para>
    /// </remarks>
    Task<bool> RefreshTokenAsync(string tenantId, CancellationToken cancellationToken = default)
        => Task.FromResult(false);
}
