namespace AzureTray.Plugin.PIM.Watchers;

// Source-agnostic shape the watcher operates on. ArmScope is the ARM resource
// path on which a PIM approval lives (e.g. "/subscriptions/{id}") — required
// to PATCH the approval stage back. Null for Entra ID approvals which always
// live at directory scope.
internal sealed record UnifiedPendingApproval(
    PimSource Source,
    string ApprovalId,
    string PrincipalDisplay,
    string RoleDisplay,
    string ScopeDisplay,
    string? ArmScope)
{
    public string DedupKey => $"{Source}:{ApprovalId}";
}
