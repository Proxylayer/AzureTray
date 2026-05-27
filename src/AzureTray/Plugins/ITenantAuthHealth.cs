using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugins;

// Tracks tenants whose token silently failed to renew at *runtime* (after the
// app is already up — distinct from the startup TenantReadinessProbe, which
// owns first-launch onboarding). When a previously-ready tenant's refresh
// token expires and the silent path throws AuthenticationRequiredException,
// the tenant enters a "needs re-auth" state that:
//   * raises a persistent popup ("Sign in" action, never auto-dismisses),
//   * surfaces a "Fix sign-in" button on the tenant's Settings row.
//
// Failures are deduplicated per tenant. A successful token acquisition (from a
// plugin call or the background monitor) or a successful interactive sign-in
// clears the state.
public interface ITenantAuthHealth
{
    // Fires whenever a tenant transitions between healthy and needs-reauth.
    // The argument is the tenant id; subscribers call NeedsReauth to read the
    // current state. May fire on a background thread — WPF subscribers must
    // marshal to the dispatcher before touching UI state.
    event Action<string>? AuthStateChanged;

    // True when the tenant is currently in the needs-reauth state.
    bool NeedsReauth(string tenantId);

    // Snapshot of every tenant currently needing re-auth.
    IReadOnlyCollection<string> FailedTenants { get; }

    // Reports that a silent token acquisition for this tenant failed because
    // interactive sign-in is required. No-op unless the tenant is currently
    // ready (so startup onboarding isn't double-prompted) and idempotent while
    // a popup is already shown / a sign-in is in progress.
    void ReportFailure(string tenantId);

    // Reports that a token was acquired successfully (silent or interactive).
    // Clears the needs-reauth state and closes any open popup. Idempotent.
    void ReportRecovered(string tenantId);

    // Runs the interactive broker sign-in for this tenant and, on success,
    // clears the needs-reauth state. Shared by the popup's "Sign in" button
    // and the Settings "Fix sign-in" button. Returns true on success.
    Task<bool> TryResolveAsync(string tenantId, CancellationToken cancellationToken);
}
