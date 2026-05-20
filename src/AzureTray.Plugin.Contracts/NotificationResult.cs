namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Base type for all notification outcomes. Switch on the runtime type:
/// <code>
/// var result = await notifier.ShowAsync(new YesNoRequest("Confirm?", "Sure?"), ct);
/// if (result is YesNoResult { Accepted: true }) { /* proceed */ }
/// </code>
/// <see cref="DismissedResult"/> is returned when the user closes the
/// notification without answering, when cancellation is requested, or when
/// the host cannot display the notification (UI not running yet).
/// </summary>
public abstract record NotificationResult;

/// <summary>Result from a <see cref="YesNoRequest"/>.</summary>
public sealed record YesNoResult(bool Accepted) : NotificationResult;

/// <summary>
/// Result from a <see cref="ChoiceRequest"/>.
/// Exactly one of <see cref="SelectedChoice"/> or <see cref="OtherText"/> is
/// non-null depending on whether the user chose from the list or typed free text.
/// </summary>
public sealed record ChoiceResult(string? SelectedChoice, string? OtherText) : NotificationResult;

/// <summary>Result from a <see cref="TextInputRequest"/>.</summary>
public sealed record TextInputResult(string Text) : NotificationResult;

/// <summary>
/// Result from an <see cref="ActionRequest"/>.
/// <see cref="ActionInvoked"/> is <c>true</c> when the user clicked the primary
/// action button; <c>false</c> when they dismissed.
/// </summary>
public sealed record ActionResult(bool ActionInvoked) : NotificationResult;

/// <summary>
/// Returned when the user dismisses without answering, on cancellation, or
/// when the host cannot display the notification.
/// </summary>
public sealed record DismissedResult : NotificationResult;
