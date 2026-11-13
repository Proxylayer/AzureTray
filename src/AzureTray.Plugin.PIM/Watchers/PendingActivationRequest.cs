using System;

namespace AzureTray.Plugin.PIM.Watchers;

// An activation the signed-in user submitted that did not go live immediately —
// it went to approval. Tracked until it is provisioned, terminally refused, or
// too old to care about. Serialized to disk, so keep it a flat record.
internal sealed record PendingActivationRequest(
    PimSource Source,
    string RequestId,
    string RoleName,
    string ScopeDisplay,
    string? ArmScope,           // Required to poll ARM request status; null for Entra ID.
    DateTimeOffset SubmittedAt,
    // Together with Source, these re-anchor the request to the eligible-role
    // row it was submitted from, so the menu can grey that row as pending.
    // Trailing optionals: null on entries persisted by older builds (before
    // the fields existed), which then simply never match a row.
    string? RoleDefinitionId = null,
    string? DirectoryScopeId = null,
    string? GroupId = null)
{
    // True when this pending request was submitted from the given eligible-role
    // row. Identity mirrors PimPlugin.RoleKey: source + role definition + the
    // scope the eligibility applies at. Entries persisted before RoleDefinitionId
    // existed never match — their rows just stay clickable.
    public bool MatchesRole(UnifiedEligibleRole role)
    {
        if (Source != role.Source || RoleDefinitionId is null) return false;
        if (!string.Equals(RoleDefinitionId, role.RoleDefinitionId, StringComparison.OrdinalIgnoreCase)) return false;

        return Source switch
        {
            PimSource.AzureRbac => string.Equals(ArmScope, role.ArmScope, StringComparison.OrdinalIgnoreCase),
            PimSource.EntraGroup => string.Equals(GroupId, role.GroupId, StringComparison.OrdinalIgnoreCase),
            // Entra ID: "/", null and absent all mean the whole directory.
            _ => string.Equals(
                EntraDirectoryScope.OrDirectory(DirectoryScopeId),
                EntraDirectoryScope.OrDirectory(role.DirectoryScopeId),
                StringComparison.OrdinalIgnoreCase),
        };
    }
}
