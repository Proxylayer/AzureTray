using System;

namespace AzureTray.Plugin.PIM.Dto;

internal sealed record EntraScheduleRequest(
    string? Id,
    string? Status,
    string? Action,
    string? PrincipalId,
    string? RoleDefinitionId,
    string? DirectoryScopeId,
    string? Justification,
    DateTimeOffset? CreatedDateTime,
    string? ApprovalId,
    string? RequestType,
    EntraPrincipal? Principal,
    EntraRoleDefinition? RoleDefinition,
    EntraScheduleInfo? ScheduleInfo);

internal sealed record EntraScheduleInfo(
    DateTimeOffset? StartDateTime,
    EntraExpiration? Expiration);

internal sealed record EntraExpiration(string? Type, string? Duration);
