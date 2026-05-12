using System;

namespace AzureTray.Plugin.PIM.Dto;

// Subset of unifiedRoleEligibilitySchedule and unifiedRoleAssignmentScheduleInstance.
// Both shapes carry principalId / roleDefinitionId / scope so we reuse the record.
internal sealed record EntraEligibilitySchedule(
    string? Id,
    string? PrincipalId,
    string? RoleDefinitionId,
    string? DirectoryScopeId,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    string? MemberType,
    EntraPrincipal? Principal,
    EntraRoleDefinition? RoleDefinition);
