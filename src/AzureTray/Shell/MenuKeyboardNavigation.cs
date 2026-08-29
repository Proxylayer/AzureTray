using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Shell;

/// <summary>
/// Pure selection-movement rules for the tray menu's keyboard navigation.
/// Kept free of any WPF types so the skip/wrap behavior is unit-testable.
/// </summary>
internal static class MenuKeyboardNavigation
{
    /// <summary>
    /// True when the row at <paramref name="index"/> can carry the keyboard
    /// highlight: a real item (not a separator) that is enabled — or disabled
    /// but carrying <see cref="PluginMenuItem.ContextItems"/>. Disabled rows
    /// with context actions must be reachable by arrow keys, otherwise
    /// Shift+F10 can never fire on them and their ONLY actions (e.g. PIM
    /// "Deactivate", JIT "Revoke access" on active rows) are mouse-only —
    /// an accessibility blocker. Enter/Space on such a row stays a no-op
    /// (see TrayMenuWindow.ActivateRowAt).
    /// </summary>
    internal static bool IsSelectable(IReadOnlyList<PluginMenuItem> items, int index)
    {
        if (index < 0 || index >= items.Count) return false;
        var item = items[index];
        return !item.IsSeparator && (item.IsEnabled || item.HasContextItems);
    }

    /// <summary>
    /// Index of the next selectable row moving from <paramref name="currentIndex"/>
    /// in <paramref name="direction"/> (+1 down / -1 up), skipping separators and
    /// disabled rows and wrapping at the ends. Returns -1 when nothing is
    /// selectable. Pass -1 as the current index for "no selection yet": Down
    /// then lands on the first selectable row, Up on the last.
    /// </summary>
    internal static int FindNextSelectableIndex(IReadOnlyList<PluginMenuItem> items, int currentIndex, int direction)
    {
        if (items.Count == 0 || direction == 0) return -1;

        var start = currentIndex;
        if (start < 0 || start >= items.Count)
        {
            // No current selection: enter the list from the edge the movement
            // direction implies.
            start = direction > 0 ? -1 : items.Count;
        }

        var index = start;
        for (var step = 0; step < items.Count; step++)
        {
            index += direction;
            if (index >= items.Count) index = 0;
            else if (index < 0) index = items.Count - 1;

            if (IsSelectable(items, index)) return index;
        }

        return -1;
    }

    /// <summary>First selectable row's index, or -1 when there is none.</summary>
    internal static int FindFirstSelectableIndex(IReadOnlyList<PluginMenuItem> items)
        => FindNextSelectableIndex(items, -1, +1);
}
