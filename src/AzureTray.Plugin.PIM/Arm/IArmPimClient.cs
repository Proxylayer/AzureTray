using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.PIM.Arm.Dto;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Policies;

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

    // Role assignments currently in force for the principal at (or inherited
    // above) each scope. Carries the endDateTime an activation lapses at.
    Task<IReadOnlyList<ArmRoleAssignmentScheduleInstance>> ListActiveRoleAssignmentsAsync(
        string principalId, IEnumerable<string> scopes, CancellationToken cancellationToken);

    // Activation policy for every role assigned a policy at each of the given
    // scopes — one request per scope, covering all roles at that scope. Keyed
    // by scope + role definition id because the same role can carry different
    // policies at different scopes. Entries absent from the result have no
    // readable policy: "unknown", not "unrestricted".
    Task<IReadOnlyDictionary<ArmRolePolicyKey, RolePolicy>> GetRolePoliciesAsync(
        IEnumerable<string> scopes, CancellationToken cancellationToken);

    // linkedRoleEligibilityScheduleId is optional on ARM's contract: pass null
    // (or blank) when the eligibility row carries no usable id and let ARM match
    // the request against the principal's eligibility itself.
    Task<ArmRoleAssignmentScheduleRequest> ActivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string? linkedRoleEligibilityScheduleId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken);

    Task<ArmRoleAssignmentScheduleRequest> DeactivateRoleAsync(
        string scope,
        string principalId,
        string roleDefinitionId,
        string justification,
        CancellationToken cancellationToken);

    // Role assignment approvals are a tenant-level ARM collection (no scope
    // segment in the URL), so no scope is taken: the approvalId alone
    // identifies the approval wherever the underlying request was made.
    Task ReviewAsync(
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken);

    Task<string?> GetActivationStatusAsync(
        string scope, string requestId, CancellationToken cancellationToken);
}
