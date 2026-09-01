using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using AzureTray.Plugin.Contracts;
using AzureTray.Plugins;

namespace AzureTray.AppRegistration;

// Single source of truth for "which scopes must a tenant's app registration
// expose?" — the host's own set (HostRequiredPermissions) plus every
// loaded plugin's declared requirements, deduplicated by (resource, scopeId).
//
// It is also the chokepoint where a malformed plugin declaration is dropped.
// PluginPermissionRequirement.ScopeId must be the GUID of the resource's
// oauth2PermissionScope, but nothing in the contract can enforce that at
// compile time, and a plugin that puts the scope *name* there makes Graph
// reject the entire requiredResourceAccess PATCH with
//   "Cannot convert the literal 'Application.Read.All' to the expected type 'Edm.Guid'"
// Because host + plugin scopes travel in one PATCH, that single bad
// declaration used to abort provisioning for the host and every other
// plugin. Filtering here keeps the blast radius to the offending plugin:
// everything else still gets declared and consented.
//
// Dropping a declaration is *not* the same as retracting it. The rejects
// are handed back to the caller (Rejected) and travel into EnsureAsync,
// which turns off stale cleanup for the resources they name — otherwise
// "not in the required list" would read as "stale", and the host would
// revoke consent the tenant already granted for a scope a plugin merely
// spelled wrong.
//
// Two host-side scope sets exist and the difference matters. Provisioning
// declares everything the host can ever need, admin tooling included, so an
// administrator running Create App Registration / Fix Permissions puts a
// complete app registration in place. Runtime asks a narrower question -
// "what must be present for the app to work?" - and the AdminTools scopes
// are deliberately not part of that: they exist only for in-app
// administration of app registrations, a non-admin user will legitimately
// never have them consented, and those features already report their own
// failures when used. Anything that judges an existing token or consent
// state (TokenFreshnessService) must therefore ask for Runtime, or it
// reports a healthy non-admin session as broken. Do not collapse these back
// into one set.
internal enum HostScopeSet
{
    // Baseline ∪ AdminTools: the full host-side set to declare on an app
    // registration. Used by Create App Registration / Fix Permissions.
    Provisioning,

    // Baseline only: the scopes the app actually needs to function.
    Runtime,
}

internal static class RequiredPermissionsAggregator
{
    internal sealed record AggregatedPermissions(
        IReadOnlyList<PluginPermissionRequirement> Required,
        IReadOnlyList<RejectedRequirement> Rejected)
    {
        // Short sentence to append to user-facing status text so the user
        // sees which plugin is at fault instead of a raw Graph error naming
        // a scope they never chose. States only what is true: the
        // declaration cannot be provisioned. It says nothing about whether
        // the scope is consented — it may well already be, via another
        // declaration or an earlier consent — and nothing was removed.
        public string RejectionNote => Rejected.Count == 0
            ? string.Empty
            : " Could not provision "
              + string.Join(", ", Rejected.Select(r => $"\"{r.ScopeName}\" from {r.Source}"))
              + " — the declaration carries a scope name where a scope GUID is required. Existing consent was left untouched.";
    }

    // Host scopes for the requested set + every loaded plugin, minus anything
    // Graph would reject. Plugin requirements are runtime requirements either
    // way, so only the host half varies with <paramref name="hostScopes"/>.
    public static AggregatedPermissions Aggregate(
        IPluginLoader pluginLoader,
        ILogger logger,
        HostScopeSet hostScopes = HostScopeSet.Provisioning)
    {
        ArgumentNullException.ThrowIfNull(pluginLoader);

        var seen = new HashSet<(PermissionApi Api, string ScopeId)>();
        var accepted = new List<PluginPermissionRequirement>();
        var rejected = new List<RejectedRequirement>();

        void AddRange(IEnumerable<PluginPermissionRequirement> source, string origin)
        {
            foreach (var p in source)
            {
                if (p is null) continue;
                if (!seen.Add((p.Api, p.ScopeId))) continue;

                if (HasValidScopeId(p))
                {
                    accepted.Add(p);
                    continue;
                }

                rejected.Add(new RejectedRequirement(origin, p.Api, p.ScopeName, p.ScopeId));
                logger.LogWarning(
                    "Plugin {PluginId} declares permission {ScopeName} with ScopeId '{ScopeId}', which is not a GUID; " +
                    "ScopeId must be the oauth2PermissionScope id. It is left out of the provisioning request so the remaining " +
                    "host and plugin scopes can still be applied; whatever consent that scope already has stays untouched.",
                    origin, p.ScopeName, p.ScopeId);
            }
        }

        AddRange(
            hostScopes == HostScopeSet.Runtime
                ? HostRequiredPermissions.Baseline
                : HostRequiredPermissions.All,
            "the host");
        foreach (var loaded in pluginLoader.LoadedPlugins)
        {
            AddRange(loaded.Plugin.RequiredPermissions, loaded.Plugin.Id);
        }

        return new AggregatedPermissions(accepted, rejected);
    }

    // Defensive guard for the Graph-facing layer: callers other than
    // Aggregate (and future ones) must not be able to poison a PATCH body
    // either. Returns the input untouched in the overwhelmingly common case
    // where everything is well-formed.
    public static IReadOnlyList<PluginPermissionRequirement> KeepValid(
        IReadOnlyList<PluginPermissionRequirement> required, ILogger logger)
    {
        if (required.Count == 0 || required.All(HasValidScopeId)) return required;

        var kept = new List<PluginPermissionRequirement>(required.Count);
        foreach (var req in required)
        {
            if (HasValidScopeId(req))
            {
                kept.Add(req);
                continue;
            }

            logger.LogWarning(
                "Ignoring required permission {ScopeName}: ScopeId '{ScopeId}' is not a GUID and Graph would reject the whole request.",
                req.ScopeName, req.ScopeId);
        }
        return kept;
    }

    public static bool HasValidScopeId(PluginPermissionRequirement requirement)
        => Guid.TryParse(requirement.ScopeId, out _);
}
