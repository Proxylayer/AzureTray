using System;

namespace AzureTray.Plugin.PIM.Groups.Dto;

// privilegedAccessGroupAssignmentScheduleInstance — membership or ownership of a
// PIM-onboarded group that is in force right now.
//
// GOTCHA: EndDateTime is a FLAT property here, not nested under
// scheduleInfo.expiration the way it is on the *request* resources. A null
// EndDateTime means the assignment is permanent (StartDateTime can be null too);
// it must never be read as "expired" or "unknown".
//
// AssignmentType distinguishes a standing assignment ("assigned") from one the
// user activated out of an eligibility ("activated"). Both grant access, so
// both gray the matching eligible row out — the value is informational.
internal sealed record GroupAssignmentScheduleInstance(
    string? Id,
    string? PrincipalId,
    string? AccessId,
    string? GroupId,
    string? MemberType,
    string? AssignmentScheduleId,
    string? AssignmentType,
    DateTimeOffset? StartDateTime,
    DateTimeOffset? EndDateTime);
