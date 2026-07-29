using System;

namespace AzureTray.Plugin.PIM.Watchers;

// Source-agnostic eligible-role record. Carries everything HandleActivationAsync
// needs to dispatch the activation back to the right API.
internal sealed record UnifiedEligibleRole(
    PimSource Source,
    string RoleName,
    string RoleDefinitionId,
    string ScopeDisplay,
    string? ArmScope,            // ARM scope path; null for Entra ID (directory-scoped).
    string? EligibilityId,       // ARM activation must reference the eligibility's ID.
    // Longest activation the role's PIM policy permits, or null when the policy
    // could not be read (403, missing rule, unparseable duration). Null is
    // "unknown", never "unlimited" — see ActivationDurationChoices. Optional so
    // cache files written before the cap existed still deserialize.
    TimeSpan? MaxActivationDuration = null,
    // How the eligibility reaches the user: "Direct" when assigned to them,
    // "Group" when inherited through a group or access package. Drives the
    // winner pick when EligibleRoleDeduplicator collapses duplicate rows —
    // a Direct row's eligibility id names the user, a group-derived one names
    // the group.
    string? MemberType = null,
    // Entra directory scope the eligibility applies at: "/" (or absent) for the
    // whole directory, otherwise something like "/administrativeUnits/{id}".
    // Null for ARM rows, which carry their scope in ArmScope. Activation must
    // send this, not a hardcoded "/", or an administrative-unit-scoped role
    // activates directory-wide (or is rejected).
    string? DirectoryScopeId = null);
