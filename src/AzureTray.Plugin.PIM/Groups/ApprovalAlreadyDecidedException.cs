using System;

namespace AzureTray.Plugin.PIM.Groups;

// A PIM for Groups approval stage can list several approvers, and the first
// decision closes the stage for all of them. A later PATCH then comes back 409
// Conflict — which is not a fault: the request the user was asked about has
// simply already been answered. Distinguished from a genuine failure so the
// watcher can say so plainly instead of raising an error the user cannot act on.
internal sealed class ApprovalAlreadyDecidedException : Exception
{
    public ApprovalAlreadyDecidedException(string approvalId, Exception? inner = null)
        : base($"Approval {approvalId} has already been decided by another approver.", inner)
    {
        ApprovalId = approvalId;
    }

    public string ApprovalId { get; }
}
