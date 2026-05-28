using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Graph;

namespace AzureTray.Plugin.PIM.Arm;

// Azure RBAC PIM operations. Most methods take an ARM "scope" — a resource
// path like "/subscriptions/{id}" or "/subscriptions/{id}/resourceGroups/{rg}".
// Pending approvals are queried per-scope (typically per subscription); the
// caller (or watcher) is responsible for enumerating relevant subscriptions
// via ListSubscriptionsAsync.
internal interface IArmPimClient
{
    Task<IReadOnlyList<ArmSubscription>> ListSubscriptionsAsync(
        CancellationToken cancellationToken);

    Task<IReadOnlyList<ArmRoleAssignmentScheduleRequest>> ListPendingApprovalsAsync(
        IEnumerable<string> scopes, CancellationToken cancellationToken);

    Task<IReadOnlyList<ArmEligibilitySchedule>> ListEligibleRolesAsync(
        string principalId, IEnumerable<string> scopes, CancellationToken cancellationToken);

    Task<bool?> CheckApprovalRequiredAsync(
        string scope, string roleDefinitionId, CancellationToken cancellationToken);

    Task<ArmRoleAssignmentScheduleRequest> ActivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string linkedRoleEligibilityScheduleId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken);

    Task<ArmRoleAssignmentScheduleRequest> DeactivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string justification,
        CancellationToken cancellationToken);

    Task ReviewAsync(
        string scope,
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken);

    Task<string?> GetActivationStatusAsync(
        string scope, string requestId, CancellationToken cancellationToken);
}
