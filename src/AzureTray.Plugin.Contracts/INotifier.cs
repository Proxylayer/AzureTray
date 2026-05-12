using System.Threading;
using System.Threading.Tasks;

namespace AzureTray.Plugin.Contracts;

// Interactive notifications. The host renders the request as a stacked popup
// near the tray and awaits the user's response. Cancellation closes the
// notification and yields a DismissedResult.
public interface INotifier
{
    Task<NotificationResult> ShowAsync(NotificationRequest request, CancellationToken cancellationToken);
}
