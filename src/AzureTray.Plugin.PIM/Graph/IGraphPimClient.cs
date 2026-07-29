using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.PIM.Dto;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Graph;

internal interface IGraphPimClient
{
    Task<string?> GetSignedInUserIdAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<EntraEligibilitySchedule>> ListActiveRoleAssignmentsAsync(
        string principalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EntraEligibilitySchedule>> ListEligibleRolesAsync(
        string principalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EntraScheduleRequest>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken);

    // Every directory-scoped role's activation policy in one request, keyed by
    // role definition id (case-insensitive). Roles absent from the result have
    // no readable policy — the caller must treat that as "unknown", not as
    // "unrestricted".
    Task<IReadOnlyDictionary<string, RolePolicy>> GetRolePoliciesAsync(
        CancellationToken cancellationToken);

    // directoryScopeId is the scope the eligibility applies at — "/" for a
    // directory-wide role, "/administrativeUnits/{id}" and the like otherwise.
    // Sending "/" for an administrative-unit-scoped eligibility asks for a
    // grant the user is not eligible for.
    Task<EntraScheduleRequest> ActivateRoleAsync(
        string principalId,
        string roleDefinitionId,
        string? directoryScopeId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken);

    Task<EntraScheduleRequest> DeactivateRoleAsync(
        string principalId,
        string roleDefinitionId,
        string? directoryScopeId,
        string justification,
        CancellationToken cancellationToken);

    Task ReviewAsync(
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken);

    Task<string?> GetActivationStatusAsync(
        string requestId, CancellationToken cancellationToken);
}
