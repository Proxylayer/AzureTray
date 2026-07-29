using System;

namespace AzureTray.Plugin.PIM.Arm.Dto;

// roleAssignmentScheduleInstances resource (subset) — a role assignment that is
// currently in force for the principal, as opposed to the eligibility that let
// them activate it. Kept separate from ArmEligibilitySchedule even though the
// shapes overlap: the two feeds answer different questions and only this one
// carries a meaningful endDateTime (when the activation lapses).
internal sealed record ArmRoleAssignmentScheduleInstance(
    string? Id,
    string? Name,
    ArmRoleAssignmentInstanceProperties? Properties);

internal sealed record ArmRoleAssignmentInstanceProperties(
    string? PrincipalId,
    string? RoleDefinitionId,
    string? Scope,
    string? Status,
    string? AssignmentType,
    string? MemberType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    ArmExpandedProperties? ExpandedProperties);
