using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

// Inputs to INotifier.ShowAsync. Use the concrete records — the host renders
// different controls based on the runtime type.
public abstract record NotificationRequest(string Title, string Message);

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
