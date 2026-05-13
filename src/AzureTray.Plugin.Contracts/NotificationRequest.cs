using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

// Inputs to INotifier.ShowAsync. Use the concrete records — the host renders
// different controls based on the runtime type. Severity is a host hint for
// the colour class — info (blue) / success (green) / warning (yellow) /
// error (red) / update (blue, with the ⬆ glyph); plugins may omit it and
// inherit Info.
public abstract record NotificationRequest(string Title, string Message)
{
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;

    // Optional structured key/value rows surfaced beneath Message inside a
    // collapsed Expander. Intended for verbose context that shouldn't bloat
    // the default notification size — exception type, HResult, response
    // body, stack trace, request-id headers, etc. Order is preserved.
    public IReadOnlyList<NotificationDetail>? Details { get; init; }
}

// A single labelled row inside a notification's Details block. Value is
// rendered as wrapping monospace text so long payloads (response bodies,
// stack traces) read cleanly.
public sealed record NotificationDetail(string Name, string Value);

// Notification colour classes. Maps to a "Bootstrap-style" alert palette in
// the host renderer:
//   Info     → blue   (default; passive informational message)
//   Success  → green  (positive confirmation — "activation succeeded")
//   Warning  → yellow (caution-but-not-failed)
//   Error    → red    (failure / blocking issue)
//   Update   → blue   (update-available; same hue as Info, but the host
//                      pairs it with an upload-arrow glyph)
//
// New enum values may be appended; existing values keep their ordinal so
// old plugins compiled against an earlier contracts version keep working.
public enum NotificationSeverity
{
    Info,
    Update,
    Warning,
    Error,
    Success,
}

// Two-button confirmation. Returns YesNoResult.
public sealed record YesNoRequest(string Title, string Message)
    : NotificationRequest(Title, Message);

// Pick-one from a fixed list, with optional "Other" free-text. Returns ChoiceResult.
// If AllowOther is true, the UI exposes an "Other:" radio with a text box; the
// result's OtherText carries the user-typed value and SelectedChoice is null.
public sealed record ChoiceRequest(
    string Title,
    string Message,
    IReadOnlyList<string> Choices,
    bool AllowOther = false)
    : NotificationRequest(Title, Message);

// Plain text input. Returns TextInputResult.
public sealed record TextInputRequest(
    string Title,
    string Message,
    string? Placeholder = null,
    string? InitialValue = null)
    : NotificationRequest(Title, Message);

// Passive toast — no input. The user dismisses it; the host may also
// auto-dismiss after a timeout. Always resolves to DismissedResult.
public sealed record InformationRequest(string Title, string Message)
    : NotificationRequest(Title, Message);

// Single-call-to-action notification. Renders a primary button labelled
// ActionLabel ("Update now", "Open file", "Retry") plus a close affordance.
// Returns ActionResult(true) if the user invokes the action, or
// DismissedResult if they close the notification. Never auto-dismisses —
// intended for moments that need explicit user attention.
public sealed record ActionRequest(string Title, string Message, string ActionLabel)
    : NotificationRequest(Title, Message);
