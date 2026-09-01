using System;

namespace AzureTray.Plugin.PIM;

// An approval stage or step can list several approvers, and the first decision
// closes it for all of them. A later PATCH then comes back 409 Conflict, and a
// later GET shows no step still in progress — neither of which is a fault: the
// request the user was asked about has simply already been answered.
// Distinguished from a genuine failure so the watcher can say so plainly
// instead of raising an error the user cannot act on.
//
// Source-agnostic on purpose: PendingApprovalWatcher catches it without knowing
// which of the three clients raised it, so it lives outside all three.
internal sealed class ApprovalAlreadyDecidedException : Exception
{
    public ApprovalAlreadyDecidedException(string approvalId, Exception? inner = null)
        : this(approvalId, detail: null, inner)
    {
    }

    // detail carries the raising client's own diagnostic wording (which resource
    // was read, what state it was in) so nothing is lost by classifying the
    // failure — the watcher surfaces the whole message under "Detail".
    public ApprovalAlreadyDecidedException(string approvalId, string? detail, Exception? inner = null)
        : base(
            string.IsNullOrWhiteSpace(detail)
                ? $"Approval {approvalId} has already been decided by another approver."
                : $"Approval {approvalId} has already been decided by another approver. {detail}",
            inner)
    {
        ApprovalId = approvalId;
    }

    public string ApprovalId { get; }
}
