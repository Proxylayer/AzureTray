using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Interactive notifications surfaced to the user as stacked popups near the
/// tray icon. The host awaits the user's response and returns the appropriate
/// <see cref="NotificationResult"/> subtype. Cancellation closes the
/// notification and returns a <see cref="DismissedResult"/>.
/// </summary>
/// <remarks>
/// <para>
/// Use the most specific <see cref="NotificationRequest"/> subtype for your
/// intent: <see cref="YesNoRequest"/> for confirmations,
/// <see cref="TextInputRequest"/> for free-text prompts,
/// <see cref="ChoiceRequest"/> for pick-one lists,
/// <see cref="ActionRequest"/> for call-to-action toasts, and
/// <see cref="InformationRequest"/> for passive toasts.
/// </para>
/// <para>
/// Set <see cref="NotificationRequest.Severity"/> to colour the notification
/// (<see cref="NotificationSeverity.Success"/> green,
/// <see cref="NotificationSeverity.Warning"/> yellow,
/// <see cref="NotificationSeverity.Error"/> red).
/// Attach <see cref="NotificationRequest.Details"/> rows for verbose context
/// that should not bloat the default notification size.
/// </para>
/// <para>
/// <strong>Security:</strong> never include raw exception messages, stack
/// traces, or internal error codes in the visible <c>Message</c> field —
/// an attacker who can trigger an error may be able to read it. Use
/// <see cref="NotificationRequest.Details"/> for diagnostics and keep
/// <c>Message</c> user-friendly.
/// </para>
/// </remarks>
public interface INotifier
{
    /// <summary>
    /// Displays <paramref name="request"/> and returns when the user responds
    /// or <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    Task<NotificationResult> ShowAsync(NotificationRequest request, CancellationToken cancellationToken);
}
