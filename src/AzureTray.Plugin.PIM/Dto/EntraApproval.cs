using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Dto;

internal sealed record EntraApproval(
    string? Id,
    string? Stage,
    List<EntraApprovalStep>? Steps);

internal sealed record EntraApprovalStep(
    string? Id,
    string? Status,
    string? ReviewedBy,
    string? ReviewResult,
    string? Justification);
