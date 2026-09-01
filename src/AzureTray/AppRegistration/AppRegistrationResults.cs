using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.AppRegistration;

public sealed record AppRegistrationInfo(string ObjectId, string AppId, string DisplayName);

// Missing: scope IDs absent from the app's requiredResourceAccess.
// NotConsented: scope names not present in any oauth2PermissionGrant for the app's service principal.
// A scope can be in one, the other, both, or neither.
public sealed record PermissionCheckResult(
    IReadOnlyList<PluginPermissionRequirement> Missing,
    IReadOnlyList<PluginPermissionRequirement> NotConsented)
{
    public bool IsFullyConfigured => Missing.Count == 0 && NotConsented.Count == 0;
}

// Replace semantics: Ensure prunes scopes that aren't in the current
// required list, matching the legacy "removes stale permissions" behavior.
// StaleScopesRemoved/StaleGrantsRemoved report how many such entries were
// pruned so the UI can surface "removed N stale scopes" messaging.
public sealed record PermissionFixResult(
    IReadOnlyList<PluginPermissionRequirement> ScopesAdded,
    IReadOnlyList<PluginPermissionRequirement> GrantsAdded,
    int StaleScopesRemoved,
    int StaleGrantsRemoved);

// Result of provisioning a new app registration end-to-end (POST
// /applications + /servicePrincipals + WAM broker URI + initial consent).
// App carries the new clientId so the caller can persist it on the tenant.
// ScopesGranted is the number of scope names the initial consent covered;
// BrokerRedirectUriAdded is false if the WAM URI PATCH failed (the app
// still exists; the user can re-run Fix Permissions later).
public sealed record AppRegistrationCreateResult(
    AppRegistrationInfo App,
    int ScopesGranted,
    bool BrokerRedirectUriAdded);

// A permission a plugin (or the host) declared that the app registration
// layer cannot provision: PluginPermissionRequirement.ScopeId must be the
// resource's oauth2PermissionScope GUID, and this one is not a GUID.
//
// Rejected declarations are carried alongside the required list rather than
// silently discarded, because "we cannot provision this" must never turn
// into "we may revoke this": EnsureAsync uses them to switch off stale
// cleanup for the resource they target, so scopes the tenant already
// consented to are left exactly as they are.
//
// Source is the plugin id (or "the host") the declaration came from —
// PluginPermissionRequirement carries no origin, so attribution is captured
// where the aggregation walks the loaded plugins.
public sealed record RejectedRequirement(
    string Source,
    PermissionApi Api,
    string ScopeName,
    string ScopeId);
