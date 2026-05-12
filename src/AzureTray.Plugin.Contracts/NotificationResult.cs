namespace AzureTray.Plugin.Contracts;

// Outputs from INotifier.ShowAsync. Switch on the runtime type:
//
//   var result = await notifier.ShowAsync(new YesNoRequest("Confirm?", "Are you sure?"), ct);
//   if (result is YesNoResult yn && yn.Accepted) { ... }
//
// DismissedResult is returned when the user closes the notification without
// answering, when cancellation is requested, or when the host cannot display
// the notification (e.g. UI not running yet).
public abstract record NotificationResult;

public sealed record YesNoResult(bool Accepted) : NotificationResult;

// One of SelectedChoice / OtherText will be non-null; the other null.
public sealed record ChoiceResult(string? SelectedChoice, string? OtherText) : NotificationResult;

public sealed record TextInputResult(string Text) : NotificationResult;

// Result from an ActionRequest. ActionInvoked is true when the user clicked
// the primary action button; false when they dismissed.
public sealed record ActionResult(bool ActionInvoked) : NotificationResult;

public sealed record DismissedResult : NotificationResult;
