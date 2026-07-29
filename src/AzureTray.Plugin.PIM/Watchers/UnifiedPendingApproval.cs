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
//
// RequestorJustification is the reason the *requestor* typed when raising the
// request — not the approver's decision comment on the approval step. Null or
// blank whenever the request was raised somewhere that doesn't insist on one
// (the Azure portal and the CLI both allow an empty justification).
internal sealed record UnifiedPendingApproval(
    PimSource Source,
    string ApprovalId,
    string PrincipalDisplay,
    string RoleDisplay,
    string ScopeDisplay,
    string? ArmScope,
    string? RequestorPrincipalId,
    string? RequestorJustification)
{
    public string DedupKey => $"{Source}:{ApprovalId}";
}
