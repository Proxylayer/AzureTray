using System;

namespace AzureTray.Plugin.PIM.Watchers;

// A role assignment currently in force for the signed-in user. Sourced per
// provider (Graph for Entra ID, ARM for Azure RBAC) so an eligible-role row is
// only marked active by an assignment from its own provider — matching on the
// display name alone used to mark an ARM row active because an unrelated Entra
// role happened to share the name.
internal sealed record ActiveRoleAssignment(
    PimSource Source,
    string RoleName,
    string? RoleDefinitionId,
    string? Scope,               // ARM scope path; null / "/" for Entra ID (directory-scoped).
    DateTimeOffset? EndDateTime) // When the activation lapses; null for permanent assignments.
{
    // True when this assignment is the one backing the given eligible-role row.
    // roleDefinitionId is the reliable key and is preferred; the display-name
    // comparison is the fallback for feeds that omit it.
    public bool Matches(UnifiedEligibleRole role)
    {
        if (Source != role.Source) return false;
        if (!IsSameRole(role)) return false;
        return Source != PimSource.AzureRbac || CoversScope(role.ArmScope);
    }

    private bool IsSameRole(UnifiedEligibleRole role)
        => !string.IsNullOrWhiteSpace(RoleDefinitionId)
            ? string.Equals(RoleDefinitionId, role.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(RoleName, role.RoleName, StringComparison.OrdinalIgnoreCase);

    // An ARM assignment grants access at its own scope and everything beneath
    // it, so a management-group-level assignment does back a subscription-level
    // eligible row. Exact match plus ancestor-prefix match, on segment
    // boundaries so "/subscriptions/abc" can't match "/subscriptions/abcdef".
    private bool CoversScope(string? rowScope)
    {
        if (string.IsNullOrWhiteSpace(Scope) || string.IsNullOrWhiteSpace(rowScope)) return false;

        var mine = Scope!.TrimEnd('/');
        var theirs = rowScope!.TrimEnd('/');
        return string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase)
            || theirs.StartsWith(mine + "/", StringComparison.OrdinalIgnoreCase);
    }
}
