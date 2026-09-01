using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AzureTray.Plugin.PIM.Graph;
using AzureTray.Plugin.PIM.Groups.Dto;
using AzureTray.Plugin.PIM.Policies;

namespace AzureTray.Plugin.PIM.Groups;

// PIM for Groups — eligible and active membership/ownership of PIM-onboarded
// groups, their activation policies, and the approver side of their activation
// requests. Deliberately separate from IGraphPimClient: the resource root, the
// notion of a "role" (accessId, not a role definition), the notion of a scope
// (a group, not a directory scope), and the approval resource (stages, not
// steps) all differ, and folding them together would make every member of both
// interfaces need a qualifier to read.
//
// The list methods take no principal id: Graph's
// filterByCurrentUser(on='principal') resolves the caller server-side, so no
// /me round-trip is needed to read the signed-in user's own access.
internal interface IGraphGroupPimClient
{
    // Eligible memberships and ownerships for the signed-in user. Each row's
    // Group is guaranteed non-null with a usable DisplayName — the client
    // resolves it separately when Graph will not expand it, and falls back to
    // the bare group id rather than dropping a row.
    Task<IReadOnlyList<GroupEligibilityScheduleInstance>> ListEligibleGroupsAsync(
        CancellationToken cancellationToken);

    // Memberships and ownerships in force right now, whether standing or
    // activated. EndDateTime is flat on this resource and null means permanent.
    Task<IReadOnlyList<GroupAssignmentScheduleInstance>> ListActiveGroupAssignmentsAsync(
        CancellationToken cancellationToken);

    // Activation policy for each of the given groups — one request per group,
    // which returns that group's member AND owner policy together, so the
    // result is keyed by both. There is no tenant-wide bulk form. Groups absent
    // from the result have no readable policy: "unknown", not "unrestricted".
    // Only ever call this for groups that appeared in the eligibility list.
    Task<IReadOnlyDictionary<GroupRolePolicyKey, RolePolicy>> GetGroupPoliciesAsync(
        IEnumerable<string> groupIds, CancellationToken cancellationToken);

    Task<GroupScheduleRequest> ActivateAsync(
        string principalId,
        string groupId,
        string accessId,
        TimeSpan duration,
        string justification,
        CancellationToken cancellationToken);

    Task<GroupScheduleRequest> DeactivateAsync(
        string principalId,
        string groupId,
        string accessId,
        string justification,
        CancellationToken cancellationToken);

    // Group activation requests waiting on the signed-in user as an approver,
    // returned as the underlying schedule requests: an approval's id IS its
    // request's id, and only the request carries the requestor, the group, and
    // the justification the approver needs to see.
    Task<IReadOnlyList<GroupScheduleRequest>> ListPendingApprovalsAsync(
        CancellationToken cancellationToken);

    // Throws ApprovalAlreadyDecidedException when another approver in the same
    // stage got there first (Graph answers 409); that is an outcome, not a
    // fault, and the caller is expected to report it as such.
    Task ReviewAsync(
        string approvalId,
        ApprovalDecision decision,
        string justification,
        CancellationToken cancellationToken);

    Task<string?> GetActivationStatusAsync(
        string requestId, CancellationToken cancellationToken);
}
