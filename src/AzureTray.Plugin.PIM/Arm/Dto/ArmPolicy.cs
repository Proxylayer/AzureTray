using System;
using System.Collections.Generic;
using AzureTray.Plugin.PIM.Policies;

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
    ArmPolicyRuleSetting? Setting) : IPolicyRule
{
    // Explicit so System.Text.Json never sees it: the rule shape is
    // deserialize-only and this is a projection, not a wire member.
    bool? IPolicyRule.IsApprovalRequired => Setting?.IsApprovalRequired;

    // ARM names the rule type exactly, with none of Graph's namespacing and no
    // case for an absent value, so this is an exact match rather than a
    // substring check.
    public bool MatchesRuleType(PolicyRuleKind kind)
    {
        var expected = kind switch
        {
            PolicyRuleKind.Expiration => "RoleManagementPolicyExpirationRule",
            _ => "RoleManagementPolicyApprovalRule",
        };

        return string.Equals(RuleType, expected, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record ArmPolicyRuleSetting(
    bool? IsApprovalRequired,
    bool? IsApprovalRequiredForExtension);
