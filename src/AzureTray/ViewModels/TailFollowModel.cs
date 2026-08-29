using System;

namespace AzureTray.ViewModels;

/// <summary>
/// Pure decision logic for the Log Viewer's smart tail-follow: whether the
/// view is pinned to the newest entry, how many entries have arrived while
/// the user was reading (paused), and how trimming of the bound collection
/// is deferred while paused so rows don't crawl out from under the viewport.
/// No WPF dependencies — unit-testable in isolation; the view model owns an
/// instance and mirrors its state into bindable properties.
/// </summary>
internal sealed class TailFollowModel
{
    /// <summary>
    /// How close (in device-independent pixels) the viewport bottom must be
    /// to the extent bottom for a user scroll to count as "at the tail".
    /// </summary>
    public const double BottomEpsilon = 24.0;

    /// <summary>True while the view should stay pinned to the newest entry.</summary>
    public bool FollowTail { get; private set; } = true;

    /// <summary>Raw entries added since the tail was paused (not filter-aware).</summary>
    public int PendingCount { get; private set; }

    /// <summary>
    /// A user-initiated scroll settled at the given position: recompute the
    /// follow state from proximity to the bottom. Returns true when the
    /// follow state changed (the caller resyncs bindings and, on a
    /// false→true transition, applies the deferred trims via <see cref="Resume"/>).
    /// </summary>
    public bool OnUserScroll(double verticalOffset, double viewportHeight, double extentHeight)
    {
        var atBottom = verticalOffset + viewportHeight >= extentHeight - BottomEpsilon;
        if (atBottom == FollowTail) return false;
        FollowTail = atBottom;
        return true;
    }

    /// <summary>
    /// Explicit pause (row selected, context menu opened). Returns true when
    /// this actually transitioned from following to paused.
    /// </summary>
    public bool Pause()
    {
        if (!FollowTail) return false;
        FollowTail = false;
        return true;
    }

    /// <summary>
    /// An entry was just added, bringing the collection to
    /// <paramref name="countAfterAdd"/>. Returns how many entries to remove
    /// from the front NOW: while following, trim to <paramref name="cap"/>
    /// as always; while paused, defer trimming (keeping the viewport
    /// anchored) but enforce <paramref name="hardCeiling"/> as a safety
    /// valve so a sustained flood cannot grow the collection unbounded.
    /// </summary>
    public int OnEntryAdded(int countAfterAdd, int cap, int hardCeiling)
    {
        if (FollowTail)
        {
            return Math.Max(0, countAfterAdd - cap);
        }

        PendingCount++;
        return Math.Max(0, countAfterAdd - hardCeiling);
    }

    /// <summary>
    /// Rejoin the tail: clears the pending counter and returns how many
    /// deferred trims to apply to bring <paramref name="currentCount"/>
    /// back down to <paramref name="cap"/>.
    /// </summary>
    public int Resume(int currentCount, int cap)
    {
        FollowTail = true;
        PendingCount = 0;
        return Math.Max(0, currentCount - cap);
    }
}
