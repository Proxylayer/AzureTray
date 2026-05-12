using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.PIM.Dto;

namespace AzureTray.Plugin.PIM.Graph;

internal interface IGraphPimClient
{
    Task<string?> GetSignedInUserIdAsync(string tenantId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EntraEligibilitySchedule>> ListActiveRoleAssignmentsAsync(
        string tenantId, string principalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EntraEligibilitySchedule>> ListEligibleRolesAsync(
        string tenantId, string principalId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EntraScheduleRequest>> ListPendingApprovalsAsync(
        string tenantId, CancellationToken cancellationToken);

    Task<bool?> CheckApprovalRequiredAsync(
        string tenantId, string roleDefinitionId, CancellationToken cancellationToken);

    Task<EntraScheduleRequest> ActivateRoleAsync(
        string tenantId,
        string principalId,
        string roleDefinitionId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken);

    Task ReviewAsync(
        string tenantId,
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken);

    Task<string?> GetActivationStatusAsync(
        string tenantId, string requestId, CancellationToken cancellationToken);
}
