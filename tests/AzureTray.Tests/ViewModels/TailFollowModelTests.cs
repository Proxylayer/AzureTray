using AzureTray.ViewModels;
using Xunit;

namespace AzureTray.Tests.ViewModels;

// Pins the Log Viewer's smart tail-follow state machine: follow starts true,
// user scrolls toggle follow based on bottom proximity (24px epsilon,
// inclusive at exactly extent - epsilon), Pause() transitions once, trimming
// is immediate-to-cap while following but deferred while paused (with the
// hard ceiling as a flood safety valve), and Resume applies the deferred
// trims and clears the pending counter.
public sealed class TailFollowModelTests
{
    private const int Cap = 500;
    private const int HardCeiling = 2000;

    [Fact]
    public void InitialState_FollowsTail_WithNoPending()
    {
        var model = new TailFollowModel();

        Assert.True(model.FollowTail);
        Assert.Equal(0, model.PendingCount);
    }

    // --- OnUserScroll -----------------------------------------------------

    [Fact]
    public void OnUserScroll_AtExactBottom_WhileFollowing_StaysFollowing_NoTransition()
    {
        var model = new TailFollowModel();

        var changed = model.OnUserScroll(verticalOffset: 900, viewportHeight: 100, extentHeight: 1000);

        Assert.False(changed);
        Assert.True(model.FollowTail);
    }

    [Fact]
    public void OnUserScroll_ExactlyAtEpsilonBoundary_CountsAsBottom()
    {
        // offset + viewport == extent - BottomEpsilon → ">=" makes it inclusive.
        var model = new TailFollowModel();
        model.Pause();

        var changed = model.OnUserScroll(
            verticalOffset: 1000 - 100 - TailFollowModel.BottomEpsilon,
            viewportHeight: 100,
            extentHeight: 1000);

        Assert.True(changed);
        Assert.True(model.FollowTail);
    }

    [Fact]
    public void OnUserScroll_JustAboveEpsilonBoundary_IsNotBottom()
    {
        var model = new TailFollowModel();

        var changed = model.OnUserScroll(
            verticalOffset: 1000 - 100 - TailFollowModel.BottomEpsilon - 0.5,
            viewportHeight: 100,
            extentHeight: 1000);

        Assert.True(changed);
        Assert.False(model.FollowTail);
    }

    [Fact]
    public void OnUserScroll_AboveEpsilon_PausesAndReportsTransition()
    {
        var model = new TailFollowModel();

        var changed = model.OnUserScroll(verticalOffset: 0, viewportHeight: 100, extentHeight: 1000);

        Assert.True(changed);
        Assert.False(model.FollowTail);
    }

    [Fact]
    public void OnUserScroll_AboveEpsilon_WhileAlreadyPaused_ReportsNoTransition()
    {
        var model = new TailFollowModel();
        model.OnUserScroll(verticalOffset: 0, viewportHeight: 100, extentHeight: 1000);

        var changed = model.OnUserScroll(verticalOffset: 50, viewportHeight: 100, extentHeight: 1000);

        Assert.False(changed);
        Assert.False(model.FollowTail);
    }

    [Fact]
    public void OnUserScroll_BackToBottom_WhilePaused_ResumesFollowing()
    {
        var model = new TailFollowModel();
        model.OnUserScroll(verticalOffset: 0, viewportHeight: 100, extentHeight: 1000);

        var changed = model.OnUserScroll(verticalOffset: 900, viewportHeight: 100, extentHeight: 1000);

        Assert.True(changed);
        Assert.True(model.FollowTail);
    }

    // --- Pause ------------------------------------------------------------

    [Fact]
    public void Pause_WhileFollowing_TransitionsOnce()
    {
        var model = new TailFollowModel();

        Assert.True(model.Pause());
        Assert.False(model.FollowTail);
    }

    [Fact]
    public void Pause_WhileAlreadyPaused_ReportsNoTransition()
    {
        var model = new TailFollowModel();
        model.Pause();

        Assert.False(model.Pause());
        Assert.False(model.FollowTail);
    }

    // --- OnEntryAdded -----------------------------------------------------

    [Fact]
    public void OnEntryAdded_Following_OverCap_TrimsDownToCap()
    {
        var model = new TailFollowModel();

        Assert.Equal(1, model.OnEntryAdded(Cap + 1, Cap, HardCeiling));
        Assert.Equal(3, model.OnEntryAdded(Cap + 3, Cap, HardCeiling));
    }

    [Fact]
    public void OnEntryAdded_Following_AtOrUnderCap_TrimsNothing()
    {
        var model = new TailFollowModel();

        Assert.Equal(0, model.OnEntryAdded(Cap, Cap, HardCeiling));
        Assert.Equal(0, model.OnEntryAdded(Cap - 1, Cap, HardCeiling));
    }

    [Fact]
    public void OnEntryAdded_Following_DoesNotIncrementPendingCount()
    {
        var model = new TailFollowModel();

        model.OnEntryAdded(Cap + 1, Cap, HardCeiling);
        model.OnEntryAdded(10, Cap, HardCeiling);

        Assert.Equal(0, model.PendingCount);
    }

    [Fact]
    public void OnEntryAdded_Paused_OverCapUnderCeiling_DefersTrimAndCountsPending()
    {
        var model = new TailFollowModel();
        model.Pause();

        Assert.Equal(0, model.OnEntryAdded(Cap + 1, Cap, HardCeiling));
        Assert.Equal(0, model.OnEntryAdded(Cap + 2, Cap, HardCeiling));
        Assert.Equal(0, model.OnEntryAdded(HardCeiling, Cap, HardCeiling));
        Assert.Equal(3, model.PendingCount);
    }

    [Fact]
    public void OnEntryAdded_Paused_OverHardCeiling_TrimsBackToCeiling()
    {
        var model = new TailFollowModel();
        model.Pause();

        // Safety valve trims count back down to the ceiling — one per add in
        // a sustained flood, more if the count somehow overshoots.
        Assert.Equal(1, model.OnEntryAdded(HardCeiling + 1, Cap, HardCeiling));
        Assert.Equal(5, model.OnEntryAdded(HardCeiling + 5, Cap, HardCeiling));
    }

    [Fact]
    public void OnEntryAdded_Paused_AtCeiling_StillCountsPendingEvenWhenTrimming()
    {
        var model = new TailFollowModel();
        model.Pause();

        model.OnEntryAdded(HardCeiling + 1, Cap, HardCeiling);
        model.OnEntryAdded(HardCeiling + 1, Cap, HardCeiling);

        Assert.Equal(2, model.PendingCount);
    }

    // --- Resume -----------------------------------------------------------

    [Fact]
    public void Resume_AfterPausedAdds_ReturnsDeferredTrims_AndResets()
    {
        var model = new TailFollowModel();
        model.Pause();
        model.OnEntryAdded(Cap + 1, Cap, HardCeiling);
        model.OnEntryAdded(Cap + 2, Cap, HardCeiling);

        var trims = model.Resume(currentCount: Cap + 2, cap: Cap);

        Assert.Equal(2, trims);
        Assert.Equal(0, model.PendingCount);
        Assert.True(model.FollowTail);
    }

    [Fact]
    public void Resume_AtOrUnderCap_TrimsNothing()
    {
        var model = new TailFollowModel();
        model.Pause();

        Assert.Equal(0, model.Resume(currentCount: Cap, cap: Cap));
        Assert.True(model.FollowTail);
    }

    [Fact]
    public void Resume_WhileAlreadyFollowing_IsIdempotent()
    {
        // No "already following" guard: it unconditionally re-pins, clears
        // pending, and reports trims-to-cap. Harmless when nothing deferred.
        var model = new TailFollowModel();

        Assert.Equal(0, model.Resume(currentCount: Cap, cap: Cap));
        Assert.True(model.FollowTail);
        Assert.Equal(0, model.PendingCount);
    }
}
