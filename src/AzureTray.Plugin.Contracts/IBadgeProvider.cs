using System;

namespace AzureTray.Plugin.Contracts;

// Plugins implement this when they have state worth surfacing on the tray
// icon itself (e.g. pending approval count). The host renders a single
// icon, aggregating across all loaded providers — typically by summing
// Count and picking the most severe State.
public interface IBadgeProvider
{
    BadgeState State { get; }

    // Non-negative count surfaced near the icon (or in the tooltip).
    // Zero means "nothing to surface"; the host treats State as canonical.
    int Count { get; }

    event Action? BadgeChanged;
}

public enum BadgeState
{
    Normal,
    Pending,
    Update,
    Error,
}
