using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Base type for all inputs to <see cref="INotifier.ShowAsync"/>. Use the
/// concrete subtypes — the host renders different controls based on the
/// runtime type.
/// </summary>
/// <remarks>
/// <para>
/// Set <see cref="Severity"/> to colour the notification (green success,
/// yellow warning, red error). Attach <see cref="Details"/> rows for verbose
/// context that shouldn't bloat the default notification size.
/// </para>
/// <para>
/// <strong>Security:</strong> keep <see cref="Message"/> user-friendly.
/// Never include raw exception messages, stack traces, HResults, or internal
/// error codes in the visible message — put diagnostics in
/// <see cref="Details"/> instead.
/// </para>
/// </remarks>
public abstract record NotificationRequest(string Title, string Message)
{
    /// <summary>
    /// Colour class for the notification chrome.
    /// Defaults to <see cref="NotificationSeverity.Info"/> (blue).
    /// </summary>
    public NotificationSeverity Severity { get; init; } = NotificationSeverity.Info;

    /// <summary>
    /// Optional structured key/value rows inside a collapsed expander below
    /// <see cref="Message"/>. Use for verbose diagnostic context — exception
    /// type, HResult, response body, request-id headers, stack trace — that
    /// should not bloat the default notification size. Order is preserved.
    /// </summary>
    public IReadOnlyList<NotificationDetail>? Details { get; init; }
}

/// <summary>
/// A single labelled row inside a <see cref="NotificationRequest.Details"/>
/// block. <see cref="Value"/> renders as wrapping monospace text.
/// </summary>
public sealed record NotificationDetail(string Name, string Value);

/// <summary>
/// Notification colour class. Maps to a Bootstrap-style alert palette.
/// </summary>
public enum NotificationSeverity
{
    /// <summary>Blue — passive informational message (default).</summary>
    Info,
    /// <summary>Blue with an upload-arrow glyph — update available.</summary>
    Update,
    /// <summary>Yellow — caution, non-blocking.</summary>
    Warning,
    /// <summary>Red — failure or blocking issue.</summary>
    Error,
    /// <summary>Green — positive confirmation ("access granted", "operation succeeded").</summary>
    Success,
}

/// <summary>
/// Two-button confirmation. Returns <see cref="YesNoResult"/>.
/// </summary>
public sealed record YesNoRequest(string Title, string Message)
    : NotificationRequest(Title, Message);

/// <summary>
/// Pick one from a fixed list, with an optional "Other" free-text fallback.
/// Returns <see cref="ChoiceResult"/>.
/// </summary>
/// <remarks>
/// When <see cref="AllowOther"/> is <c>true</c>, the UI shows an "Other:"
/// radio with a text box; the result's <c>OtherText</c> carries the user
/// value and <c>SelectedChoice</c> is <c>null</c>.
/// </remarks>
public sealed record ChoiceRequest(
    string Title,
    string Message,
    IReadOnlyList<string> Choices,
    bool AllowOther = false)
    : NotificationRequest(Title, Message);

/// <summary>
/// Plain text input. Returns <see cref="TextInputResult"/>.
/// </summary>
public sealed record TextInputRequest(
    string Title,
    string Message,
    string? Placeholder = null,
    string? InitialValue = null)
    : NotificationRequest(Title, Message);

/// <summary>
/// Passive toast — no input required. The user dismisses it; the host may
/// also auto-dismiss after a timeout. Always resolves to
/// <see cref="DismissedResult"/>.
/// </summary>
public sealed record InformationRequest(string Title, string Message)
    : NotificationRequest(Title, Message);

/// <summary>
/// Single call-to-action notification. Renders a primary button labelled
/// <see cref="ActionLabel"/> plus a close affordance. Returns
/// <see cref="ActionResult"/>(<c>true</c>) if the user invokes the action,
/// or <see cref="DismissedResult"/> if they close it. Never auto-dismisses.
/// </summary>
public sealed record ActionRequest(string Title, string Message, string ActionLabel)
    : NotificationRequest(Title, Message);
