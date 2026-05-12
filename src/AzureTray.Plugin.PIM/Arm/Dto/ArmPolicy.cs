using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmPolicyAssignment(
    string? Id,
    string? Name,
    ArmPolicyAssignmentProperties? Properties);

internal sealed record ArmPolicyAssignmentProperties(
    string? PolicyId,
    string? RoleDefinitionId);

internal sealed record ArmPolicyResponse(
    string? Id,
    string? Name,
    ArmPolicyResponseProperties? Properties);

internal sealed record ArmPolicyResponseProperties(
    List<ArmPolicyRule>? Rules);

internal sealed record ArmPolicyRule(
    string? Id,
    string? RuleType,
    ArmPolicyRuleSetting? Setting);

internal sealed record ArmPolicyRuleSetting(
    bool? IsApprovalRequired,
    bool? IsApprovalRequiredForExtension);
