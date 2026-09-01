using System;
using System.Diagnostics;
using System.Threading.Tasks;

namespace AzureTray.Tests;

/// <summary>
/// Waits for out-of-band work to become observable.
/// </summary>
/// <remarks>
/// Several components dispatch work and return without awaiting it - a watcher
/// fires <c>_ = HandleNewApprovalAsync(...)</c> from inside its poll, for
/// instance - so a test has to wait before it can assert. Sleeping a fixed
/// number of milliseconds bets the work lands inside that window: too short and
/// the suite flakes on a loaded machine, too long and every test pays the worst
/// case on every run. Polling a condition costs only as long as the work
/// actually takes and still tolerates a slow machine, so it is simultaneously
/// faster and steadier.
/// </remarks>
internal static class Settle
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(1);

    // A satisfied condition only establishes "at least this much has happened",
    // and callers go on to assert an exact call count. Without a moment's grace
    // the assertion runs the instant the expected call lands, so a duplicate
    // that was already in flight would never be seen and a dedupe regression
    // would pass.
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// Returns as soon as <paramref name="condition"/> holds, giving up after
    /// <paramref name="timeout"/>. A timeout is deliberately not an error here:
    /// the caller's own assertion runs next and reports what was missing far
    /// better than this helper could.
    /// </summary>
    internal static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var limit = timeout ?? DefaultTimeout;
        var elapsed = Stopwatch.StartNew();
        while (!Safe(condition) && elapsed.Elapsed < limit)
        {
            await Task.Delay(PollInterval).ConfigureAwait(false);
        }

        await Task.Delay(Grace).ConfigureAwait(false);
    }

    /// <summary>
    /// A bounded wait for the few assertions that prove something did *not*
    /// happen. There is no condition to poll for an absence, so a plain wait is
    /// the only option; it is kept short because its only job is to let a stray
    /// dispatch arrive and be caught.
    /// </summary>
    internal static Task QuietAsync() => Task.Delay(TimeSpan.FromMilliseconds(50));

    // Conditions typically inspect a substitute's recorded calls while the code
    // under test may still be adding to them. A transient enumeration failure
    // means "not yet", not "test error" - the next poll re-reads it.
    private static bool Safe(Func<bool> condition)
    {
        try { return condition(); }
        catch (InvalidOperationException) { return false; }
    }
}
