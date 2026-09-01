using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace AzureTray.Auth;

// Once-then-quiet gate for the periodic staleness check, in the shape of the
// PIM plugin's FetchFailureGate: the state transitions carry information, the
// steady state does not.
//
// The case it exists for: a plugin declares a scope the tenant will never
// grant (no admin consent, or an administrator who declines it). Every cycle
// then finds the same scope missing from the token, tries the same refresh,
// and fails identically — one failed refresh per tenant per cycle, forever,
// each one a Warning. That is the log-spam pattern this codebase has been
// bitten by before.
//
// So an act is allowed only when the tenant's missing-scope set contains
// something the gate has not already acted on. A set that repeats — or
// shrinks, because part of it was healed — stays quiet. A genuinely new
// missing scope re-opens the gate on its own, and Rearm re-opens it for
// everything the user just tried to change (Fix permissions, Refresh
// tokens); an app restart re-opens it by construction, since this is
// in-memory only.
//
// Public only because SettingsViewModel's constructor is: the view model is
// resolved by the container, which needs a public constructor, and this is
// one of its parameters. Nothing outside the host assembly uses it.
public sealed class TokenFreshnessGate
{
    // tenantId -> scope names already acted on. Values are replaced, never
    // mutated in place, so concurrent readers always see a consistent set.
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _acted =
        new(StringComparer.OrdinalIgnoreCase);

    // True when at least one of the missing scopes has not been acted on for
    // this tenant yet. An empty missing set is never worth acting on.
    public bool ShouldAct(string tenantId, IReadOnlyCollection<string> missingScopes)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || missingScopes.Count == 0) return false;
        if (!_acted.TryGetValue(tenantId, out var already)) return true;

        return missingScopes.Any(scope => !already.Contains(scope));
    }

    // Records what was just acted on so an identical or smaller missing set
    // stays quiet until something re-arms the gate.
    public void RecordActed(string tenantId, IReadOnlyCollection<string> missingScopes)
    {
        if (string.IsNullOrWhiteSpace(tenantId) || missingScopes.Count == 0) return;

        _acted.AddOrUpdate(
            tenantId,
            _ => new HashSet<string>(missingScopes, StringComparer.OrdinalIgnoreCase),
            (_, existing) => new HashSet<string>(existing.Concat(missingScopes), StringComparer.OrdinalIgnoreCase));
    }

    // Re-opens the gate for a tenant. Called when the user has changed
    // something the gate's verdict was based on — a Fix permissions run or a
    // Refresh tokens click — and by the checker itself when a tenant comes
    // back clean, so a later regression is reported rather than swallowed.
    public void Rearm(string tenantId)
    {
        if (string.IsNullOrWhiteSpace(tenantId)) return;
        _acted.TryRemove(tenantId, out _);
    }
}
