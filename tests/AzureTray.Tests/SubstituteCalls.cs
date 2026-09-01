using System.Linq;
using AzureTray.Plugin.Contracts;
using NSubstitute;

namespace AzureTray.Tests;

/// <summary>
/// Reads how many times a substitute has been called so far, so a test can
/// poll for out-of-band work with <see cref="Settle.UntilAsync"/>.
/// </summary>
/// <remarks>
/// NSubstitute's own <c>Received(n)</c> asserts rather than reports - it throws
/// when the count is wrong, which is what a test wants at the end but not what
/// a wait condition can use. These read the recorded calls instead and leave
/// the assertion to the caller.
/// </remarks>
internal static class SubstituteCalls
{
    /// <summary>Notifications of type <typeparamref name="T"/> shown so far.</summary>
    internal static int Shown<T>(INotifier notifier) where T : NotificationRequest
        => notifier.ReceivedCalls().Count(c =>
            c.GetMethodInfo().Name == nameof(INotifier.ShowAsync)
            && c.GetArguments()[0] is T);

    /// <summary>
    /// Approval reviews submitted so far. Takes <see cref="object"/> because the
    /// Graph, ARM and group PIM clients each declare their own
    /// <c>ReviewAsync</c> without a shared interface to constrain to.
    /// </summary>
    internal static int Reviews(object pimClient)
        => pimClient.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "ReviewAsync");
}
