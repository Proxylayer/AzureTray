using System;

namespace AzureTray.Plugin.PIM.Arm.Dto;

internal sealed record ArmEligibilitySchedule(
    string? Id,
    string? Name,
    ArmEligibilityProperties? Properties);

internal sealed record ArmEligibilityProperties(
    string? PrincipalId,
    string? RoleDefinitionId,
    string? Scope,
    string? Status,
    string? MemberType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    ArmExpandedProperties? ExpandedProperties);
