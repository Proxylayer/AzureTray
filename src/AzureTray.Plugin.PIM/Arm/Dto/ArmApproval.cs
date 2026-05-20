using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmApproval(
    string? Id,
    string? Name,
    ArmApprovalProperties? Properties);

internal sealed record ArmApprovalProperties(
    List<ArmApprovalStage>? Stages);

internal sealed record ArmApprovalStage(
    string? Id,
    string? Name,
    ArmApprovalStageProperties? Properties);

internal sealed record ArmApprovalStageProperties(
    string? Status,
    string? ReviewResult,
    string? Justification,
    bool? AssignedToMe);
