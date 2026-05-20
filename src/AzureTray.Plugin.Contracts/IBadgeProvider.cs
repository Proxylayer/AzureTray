using System;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// Optional interface for plugins that have state worth surfacing on the
/// tray icon badge (e.g. active rule count, pending approvals). The host
/// aggregates across all loaded providers — typically by summing
/// <see cref="Count"/> and picking the most severe <see cref="State"/>.
/// </summary>
/// <remarks>
/// Fire <see cref="BadgeChanged"/> from any thread whenever <see cref="Count"/>
/// or <see cref="State"/> changes; the host marshals to the UI thread.
/// </remarks>
public interface IBadgeProvider
{
    /// <summary>
    /// Visual state of the badge. The host maps this to a colour/glyph:
    /// <see cref="BadgeState.Normal"/> (neutral),
    /// <see cref="BadgeState.Pending"/> (amber — attention needed),
    /// <see cref="BadgeState.Update"/> (blue arrow — update available),
    /// <see cref="BadgeState.Error"/> (red — failure).
    /// </summary>
    BadgeState State { get; }

    /// <summary>
    /// Non-negative count surfaced near the icon or in the tooltip.
    /// Zero means nothing to surface; the host treats <see cref="State"/>
    /// as the canonical signal.
    /// </summary>
    int Count { get; }

    /// <summary>Fired when <see cref="Count"/> or <see cref="State"/> changes.</summary>
    event Action? BadgeChanged;
}

/// <summary>Visual state of an <see cref="IBadgeProvider"/> badge.</summary>
public enum BadgeState
{
    /// <summary>Neutral — no outstanding items.</summary>
    Normal,
    /// <summary>Amber — items exist and need attention.</summary>
    Pending,
    /// <summary>Blue arrow — an update is available.</summary>
    Update,
    /// <summary>Red — an error or failure is present.</summary>
    Error,
}
