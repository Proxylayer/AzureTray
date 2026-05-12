using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace AzureTray.Plugin.Contracts;

// What the host gives each plugin on initialization. Every property is stable
// surface across the major version of PluginApiVersion; adding members requires
// either default-implemented interface methods or an ApiVersion bump.
public interface IPluginContext
{
    ILogger Logger { get; }

    // Authenticated HTTP egress. The host owns the named clients and credentials.
    IPluginHttpClient Http { get; }

    // Interactive notifications surfaced to the user near the tray icon.
    INotifier Notifier { get; }

    // System clipboard adapter. Used by plugins that need to put a value
    // (e.g. a LAPS password) onto the clipboard without referencing the
    // WPF / WinForms clipboard APIs themselves.
    IClipboard Clipboard { get; }

    // Read-only snapshot of tenants the user has configured in the host. Use
    // TenantReady / TenantRemoved to react to changes after InitializeAsync.
    IReadOnlyList<PluginTenant> Tenants { get; }

    // Tenants for which the host has confirmed auth works (a token was
    // successfully acquired). Plugins MUST NOT call Http.SendAsync against a
    // tenant until they see it in this set or receive a TenantReady event.
    // Iterate this at InitializeAsync to backfill watchers for tenants that
    // became ready before the plugin loaded.
    IReadOnlyList<PluginTenant> ReadyTenants { get; }

    bool IsTenantReady(string tenantId);

    // Fired when a tenant transitions to ready (token acquired successfully)
    // or is removed from the host's configuration. Subscribers may be invoked
    // on a thread-pool thread; marshal as needed.
    event Action<PluginTenant> TenantReady;
    event Action<string> TenantRemoved;

    // OAuth scope strings resolved from the host's cloud configuration.
    // Use these with IPluginHttpClient.SendAsync so calls work in sovereign
    // clouds as well as public Azure.
    string GraphScope { get; }
    string ArmScope { get; }

    // Host-managed per-plugin scratch directory. Plugins may freely read/write
    // here — typical use is caching last-known state so a restart doesn't
    // present a blank menu while the first poll runs. The host creates the
    // directory before passing it; the path survives across plugin reloads
    // (it's NOT inside the Velopack-versioned install root).
    string DataDir { get; }

    // SemVer string of the running host (e.g. "0.2.0"). Plugins use this to
    // conditionally enable features that depend on host capabilities
    // introduced after their PluginApiVersion baseline.
    //
    // Defaults to null so older hosts that don't supply it stay compatible
    // with plugins built against a newer Contracts package; a plugin that
    // observes null should fall back to the safe / minimum behaviour.
    string? HostVersion => null;
}
