using System;

namespace AzureTray.Plugin.PIM.Arm;

// Trailing-segment reader for the ARM resource paths this plugin has to reduce
// to a bare id.
internal static class ArmResourceId
{
    // ARM returns approvalId as a scope-relative resource path such as
    // "/providers/Microsoft.Authorization/roleAssignmentApprovals/{guid}".
    // Extract the trailing GUID segment so URL construction in ReviewAsync
    // doesn't double-up the provider path. A schedule request's id is reduced
    // the same way when the response carried no separate name.
    //
    // Null/blank in, null out — a caller with no id gets nothing rather than an
    // empty segment.
    public static string? LastSegment(string? resourceId)
    {
        if (string.IsNullOrWhiteSpace(resourceId)) return null;
        var last = resourceId.LastIndexOf('/');
        return last >= 0 && last < resourceId.Length - 1 ? resourceId[(last + 1)..] : resourceId;
    }
}
