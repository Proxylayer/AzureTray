using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmPolicyAssignment(
    string? Id,
    string? Name,
    ArmPolicyAssignmentProperties? Properties);

// EffectiveRules comes back inline on every assignment in the
// roleManagementPolicyAssignments listing — "effective" meaning inherited
// policy is already folded in. Reading it here is what makes the separate
// GET {policyId} round-trip unnecessary.
internal sealed record ArmPolicyAssignmentProperties(
    string? PolicyId,
    string? RoleDefinitionId,
    List<ArmPolicyRule>? EffectiveRules);

// Union of the rule shapes we read. ARM discriminates with RuleType, and rules
// are additionally identified by Id ("Expiration_EndUser_Assignment",
// "Approval_EndUser_Assignment"). Note MaximumDuration sits directly on the
// rule while approval settings are nested under Setting.
internal sealed record ArmPolicyRule(
    string? Id,
    string? RuleType,
    string? MaximumDuration,
    bool? IsExpirationRequired,
    ArmPolicyRuleSetting? Setting);

internal sealed record ArmPolicyRuleSetting(
    bool? IsApprovalRequired,
    bool? IsApprovalRequiredForExtension);
