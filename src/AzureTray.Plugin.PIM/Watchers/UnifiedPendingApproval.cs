namespace AzureTray.Plugin.PIM.Watchers;

// Source-agnostic shape the watcher operates on. ArmScope is the ARM resource
// path on which a PIM approval lives (e.g. "/subscriptions/{id}") — required
// to PATCH the approval stage back. Null for Entra ID approvals which always
// live at directory scope.
//
// RequestorPrincipalId is the objectId of the user who created the approval
// request. Used to drop the watcher's "you're being asked to approve" prompt
// when the requestor IS the signed-in user (Azure RBAC PIM will surface a
// user's own request to themselves as eligible reviewers — Entra approval
// policies can do the same when no other approver matches the policy).
internal sealed record UnifiedPendingApproval(
    PimSource Source,
    string ApprovalId,
    string PrincipalDisplay,
    string RoleDisplay,
    string ScopeDisplay,
    string? ArmScope,
    string? RequestorPrincipalId)
{
    public string DedupKey => $"{Source}:{ApprovalId}";
}
