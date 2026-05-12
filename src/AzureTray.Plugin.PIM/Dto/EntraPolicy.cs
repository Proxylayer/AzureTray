using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Dto;

internal sealed record EntraPolicyAssignment(
    string? Id,
    string? PolicyId,
    string? RoleDefinitionId,
    string? ScopeId,
    string? ScopeType);

internal sealed record EntraApprovalRule(
    string? Id,
    EntraApprovalRuleSetting? Setting);

internal sealed record EntraApprovalRuleSetting(
    bool? IsApprovalRequired,
    bool? IsApprovalRequiredForExtension,
    List<EntraApprovalStage>? ApprovalStages);

internal sealed record EntraApprovalStage(
    bool? IsApproverJustificationRequired,
    int? ApprovalStageTimeOutInDays);
