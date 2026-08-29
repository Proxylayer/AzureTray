using AzureTray.Plugin.PIM.Watchers;
using Xunit;

namespace AzureTray.Tests.PimPlugin;

// Pins the once-then-quiet gate: first failure warns with the exception,
// repeats drop to one Debug line, already-reported conditions are quiet from
// the first occurrence, and recovery is reported exactly once.
public sealed class FetchFailureGateTests
{
    [Fact]
    public void FirstFailure_WarnsWithException()
    {
        var gate = new FetchFailureGate();

        Assert.Equal(
            FetchFailureGate.FailureLog.WarnWithException,
            gate.RecordFailure(knownCondition: false));
    }

    [Fact]
    public void ConsecutiveFailures_DropToDebugOneLine()
    {
        var gate = new FetchFailureGate();
        gate.RecordFailure(knownCondition: false);

        Assert.Equal(
            FetchFailureGate.FailureLog.DebugOneLine,
            gate.RecordFailure(knownCondition: false));
        Assert.Equal(
            FetchFailureGate.FailureLog.DebugOneLine,
            gate.RecordFailure(knownCondition: false));
    }

    [Fact]
    public void KnownCondition_IsDebugOneLineEvenOnFirstFailure()
    {
        var gate = new FetchFailureGate();

        Assert.Equal(
            FetchFailureGate.FailureLog.DebugOneLine,
            gate.RecordFailure(knownCondition: true));
    }

    [Fact]
    public void RecordSuccess_WhenHealthy_ReportsNoRecovery()
    {
        var gate = new FetchFailureGate();

        Assert.False(gate.RecordSuccess());
        Assert.False(gate.RecordSuccess());
    }

    [Fact]
    public void RecordSuccess_AfterFailingStreak_ReportsRecoveryOnce()
    {
        var gate = new FetchFailureGate();
        gate.RecordFailure(knownCondition: false);
        gate.RecordFailure(knownCondition: false);

        Assert.True(gate.RecordSuccess());
        Assert.False(gate.RecordSuccess());
    }

    [Fact]
    public void FailureAfterRecovery_WarnsAgain()
    {
        var gate = new FetchFailureGate();
        gate.RecordFailure(knownCondition: false);
        gate.RecordSuccess();

        Assert.Equal(
            FetchFailureGate.FailureLog.WarnWithException,
            gate.RecordFailure(knownCondition: false));
    }

    [Fact]
    public void UnknownFailureThenKnownCondition_StaysDebugOneLine()
    {
        // Pins actual behavior: once failing, a knownCondition failure is
        // quiet regardless — the gate tracks only the failing streak, not
        // which kind of failure started it.
        var gate = new FetchFailureGate();
        gate.RecordFailure(knownCondition: false);

        Assert.Equal(
            FetchFailureGate.FailureLog.DebugOneLine,
            gate.RecordFailure(knownCondition: true));
    }

    [Fact]
    public void KnownConditionThenUnknownFailure_IsDebugOneLine()
    {
        // Pins actual behavior: a knownCondition failure marks the gate as
        // failing, so a subsequent UNKNOWN failure never gets its
        // WarnWithException — it rides the existing streak.
        var gate = new FetchFailureGate();
        gate.RecordFailure(knownCondition: true);

        Assert.Equal(
            FetchFailureGate.FailureLog.DebugOneLine,
            gate.RecordFailure(knownCondition: false));
    }

    [Fact]
    public void KnownConditionStreakThenSuccess_StillReportsRecovered()
    {
        // Pins actual behavior: knownCondition failures set _failing, so the
        // ending success reports recovery (Information) even though every
        // failure in the streak was logged only at Debug.
        var gate = new FetchFailureGate();
        gate.RecordFailure(knownCondition: true);
        gate.RecordFailure(knownCondition: true);

        Assert.True(gate.RecordSuccess());
    }
}
