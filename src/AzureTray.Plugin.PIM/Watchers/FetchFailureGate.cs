namespace AzureTray.Plugin.PIM.Watchers;

// Once-then-quiet gate for a per-poll fetch that can fail identically every
// interval for hours (an expired refresh token repeats every ~60s poll until
// the user re-authenticates — that pattern alone once produced 11k+ log lines
// in 14 days). The state transitions are what carry information; the steady
// state does not:
//
//   success -> failure   log once at Warning WITH the exception,
//   failing -> failing   log at Debug, one line, no stack,
//   failure -> success   log once at Information ("recovered").
//
// A failure whose condition is already reported elsewhere (knownCondition,
// e.g. "needs interactive sign-in", which the host's TenantAuthHealthService
// logs once at Warning and surfaces as a re-auth prompt) is quiet from the
// FIRST occurrence.
//
// One instance per fetch source; in-memory only, not thread-safe — each
// watcher polls its sources from a single loop.
internal sealed class FetchFailureGate
{
    internal enum FailureLog
    {
        // First failure after a success (or ever): Warning with the exception.
        WarnWithException,

        // Still failing, or a condition another component already reported:
        // one Debug line, exception type + message inline, no stack.
        DebugOneLine,
    }

    private bool _failing;

    public FailureLog RecordFailure(bool knownCondition)
    {
        var wasFailing = _failing;
        _failing = true;
        return wasFailing || knownCondition
            ? FailureLog.DebugOneLine
            : FailureLog.WarnWithException;
    }

    // True when this success ends a failing streak — the caller should log
    // the recovery once at Information.
    public bool RecordSuccess()
    {
        var recovered = _failing;
        _failing = false;
        return recovered;
    }
}
