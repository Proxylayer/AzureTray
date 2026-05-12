namespace AzureTray.Plugin.PIM.Watchers;

// Source-agnostic eligible-role record. Carries everything HandleActivationAsync
// needs to dispatch the activation back to the right API.
internal sealed record UnifiedEligibleRole(
    PimSource Source,
    string RoleName,
    string RoleDefinitionId,
    string ScopeDisplay,
    string? ArmScope,            // ARM scope path; null for Entra ID (directory-scoped).
    string? EligibilityId);      // ARM activation must reference the eligibility's ID.
