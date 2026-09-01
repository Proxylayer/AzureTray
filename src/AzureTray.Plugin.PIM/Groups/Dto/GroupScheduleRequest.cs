using System;
using AzureTray.Plugin.PIM.Dto;

namespace AzureTray.Plugin.PIM.Groups.Dto;

// privilegedAccessGroupAssignmentScheduleRequest — the resource a selfActivate,
// selfDeactivate, or another user's activation request is represented by.
//
// The approver feed reads these too: an approval's id IS the id of the request
// it approves, so the request is what carries the requestor, the group, and the
// justification the approval object itself does not.
//
// Status is an open string set — match it case-insensitively. "PendingApproval"
// is spelled identically to the directory-role status, so ActivationStatus
// classifies both without a group-specific branch.
internal sealed record GroupScheduleRequest(
    string? Id,
    string? Status,
    string? Action,
    string? AccessId,
    string? PrincipalId,
    string? GroupId,
    string? Justification,
    string? ApprovalId,
    string? TargetScheduleId,
    DateTimeOffset? CreatedDateTime,
    DateTimeOffset? CompletedDateTime,
    EntraPrincipal? Principal,
    GroupRef? Group);

// Narrow projection for the status poll, which asks for nothing else.
internal sealed record GroupScheduleRequestStatus(string? Id, string? Status);
