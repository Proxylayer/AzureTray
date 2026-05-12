using System;

namespace AzureTray.Plugin.PIM.Arm.Dto;

// roleAssignmentScheduleRequests resource (subset). Properties are nested
// under "properties" per ARM convention.
internal sealed record ArmRoleAssignmentScheduleRequest(
    string? Id,
    string? Name,
    string? Type,
    ArmRoleRequestProperties? Properties);

internal sealed record ArmRoleRequestProperties(
    string? Status,
    string? PrincipalId,
    string? RoleDefinitionId,
    string? Scope,
    string? Justification,
    string? RequestType,
    string? ApprovalId,
    DateTimeOffset? CreatedOn,
    ArmExpandedProperties? ExpandedProperties,
    ArmScheduleInfo? ScheduleInfo,
    string? LinkedRoleEligibilityScheduleId);

internal sealed record ArmExpandedProperties(
    ArmPrincipalDto? Principal,
    ArmRoleDefinitionDto? RoleDefinition,
    ArmScopeDto? Scope);

internal sealed record ArmPrincipalDto(string? Id, string? DisplayName, string? Type, string? Email);

internal sealed record ArmRoleDefinitionDto(string? Id, string? DisplayName, string? Type);

internal sealed record ArmScopeDto(string? Id, string? DisplayName, string? Type);

internal sealed record ArmScheduleInfo(
    DateTimeOffset? StartDateTime,
    ArmExpiration? Expiration);

internal sealed record ArmExpiration(string? Type, string? Duration);
