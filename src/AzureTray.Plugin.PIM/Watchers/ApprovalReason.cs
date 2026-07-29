using System;

namespace AzureTray.Plugin.PIM.Watchers;

// The requestor's justification, rendered for an approver's notification.
//
// MessageLine always carries something printable, so it can be concatenated
// into the notification's Message slot unconditionally — the requestor's own
// words when there are any, an explicit "no reason" line when there are not.
// A reason placed only in the Details expander would be one click away from
// invisible (the expander starts collapsed), so the visible Message is where
// it goes.
//
// ClampedFullText is non-null only when MessageLine had to truncate, and
// carries the untruncated reason for the Details expander — which has its own
// ScrollViewer, so an essay is safe there.
internal sealed record ApprovalReason(string MessageLine, string? ClampedFullText)
{
    // Roughly four lines at the notification window's 360px width. Long
    // enough for any real justification, short enough that the choice list
    // and the action buttons keep their room.
    internal const int MaxMessageLength = 280;

    internal static ApprovalReason From(string? justification)
    {
        var text = Collapse(justification);
        if (text is null) return new ApprovalReason("No reason was given for this request.", null);

        return text.Length <= MaxMessageLength
            ? new ApprovalReason($"Reason: \"{text}\"", null)
            : new ApprovalReason($"Reason: \"{text[..MaxMessageLength].TrimEnd()}…\"", text);
    }

    // Justifications arrive as free text and may carry newlines or padding.
    // Collapsing every run of whitespace to a single space keeps the Message
    // slot a predictable few lines and makes the length clamp meaningful.
    // Returns null for "no reason given".
    private static string? Collapse(string? justification)
    {
        if (string.IsNullOrWhiteSpace(justification)) return null;
        var words = justification.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words);
    }
}
