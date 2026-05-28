using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.PIM.Dto;

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

    Task<bool?> CheckApprovalRequiredAsync(
        string roleDefinitionId, CancellationToken cancellationToken);

    Task<EntraScheduleRequest> ActivateRoleAsync(
        string principalId,
        string roleDefinitionId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken);

    Task<EntraScheduleRequest> DeactivateRoleAsync(
        string principalId,
        string roleDefinitionId,
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
