using System;
using System.Collections.Generic;

namespace AzureTray.Plugin.PIM.Groups.Dto;

// The approval object behind a group activation request awaiting a decision.
//
// The child resource is STAGES, not the steps a directory-role approval uses —
// different collection name, different URL segment, and the stages come back
// inline on a GET with no $expand needed.
internal sealed record GroupApproval(
    string? Id,
    List<GroupApprovalStage>? Stages);

// ReviewResult is "NotReviewed" until somebody decides, then "Approve" / "Deny"
// — PascalCase on the wire in both directions. Status is "InProgress" while the
// stage is open and "Completed" once any approver in it has decided, which is
// why a second approver's PATCH comes back 409.
//
// reviewedBy is deliberately not modelled: Graph sends it as an identity OBJECT,
// and a record member typed string would throw while deserializing the whole
// approval. Nothing here needs it — the stage id and status are what pick the
// stage to PATCH.
internal sealed record GroupApprovalStage(
    string? Id,
    string? DisplayName,
    DateTimeOffset? ReviewedDateTime,
    string? ReviewResult,
    string? Status,
    bool? AssignedToMe,
    string? Justification);
