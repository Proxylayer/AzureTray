using System;

namespace AzureTray.Plugin.PIM.Groups.Dto;

// privilegedAccessGroupEligibilityScheduleInstance — one eligible membership or
// ownership of one PIM-onboarded group.
//
// There is no roleDefinitionId and no directoryScopeId here, unlike a directory
// role: the "role" is AccessId ("member" or "owner") and the scope is GroupId.
// A group therefore has exactly two things a user can be eligible for.
//
// Group is populated only when the request expanded it — and GraphGroupPimClient
// guarantees it is filled in by the time a caller sees the row, resolving the
// display name separately when the expansion is unavailable, so the menu never
// has to know which path produced the name.
internal sealed record GroupEligibilityScheduleInstance(
    string? Id,
    string? PrincipalId,
    string? AccessId,
    string? GroupId,
    string? MemberType,
    string? EligibilityScheduleId,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime,
    GroupRef? Group);

// The subset of the group resource the menu needs. Same shape wherever a group
// is expanded onto a PIM resource, and what GetGroupDisplayNamesAsync reads.
internal sealed record GroupRef(string? Id, string? DisplayName);
