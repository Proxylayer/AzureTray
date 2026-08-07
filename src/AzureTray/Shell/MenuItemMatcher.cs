using System;
using System.Collections.Generic;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Shell;

// Pure matching logic for refreshing an already-open submenu chain when the
// menu is rebuilt (plugin fired MenuChanged while the user has a submenu
// open). Deliberately WPF-free so it is unit-testable without a dispatcher.
internal static class MenuItemMatcher
{
    /// <summary>
    /// Finds, in a freshly rebuilt item collection, the item that corresponds
    /// to <paramref name="oldItem"/> — the parent of a currently-open submenu
    /// or context popup. Returns <c>null</c> when no counterpart exists any
    /// more (caller should close the orphaned submenu).
    /// </summary>
    /// <remarks>
    /// Matching rule, first hit wins, separators never match:
    /// <list type="number">
    /// <item><see cref="PluginMenuItem.Key"/> equality (ordinal) when the old
    /// item has a Key — authoritative: if the Key is gone, the item is gone,
    /// and there is no fallback to Text.</item>
    /// <item>Exact <see cref="PluginMenuItem.Text"/> equality (ordinal).</item>
    /// <item>Text equality after stripping a trailing "(n)" count suffix from
    /// both sides — tolerates labels like "Pending Approvals (3)" whose count
    /// changes across rebuilds (observed in the PIM plugin).</item>
    /// </list>
    /// </remarks>
    public static PluginMenuItem? FindRefreshedParent(
        PluginMenuItem oldItem,
        IReadOnlyList<PluginMenuItem> newItems)
    {
        ArgumentNullException.ThrowIfNull(oldItem);
        ArgumentNullException.ThrowIfNull(newItems);

        if (oldItem.Key is not null)
        {
            foreach (var candidate in newItems)
            {
                if (candidate.IsSeparator) continue;
                if (string.Equals(candidate.Key, oldItem.Key, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            return null;
        }

        foreach (var candidate in newItems)
        {
            if (candidate.IsSeparator || candidate.Key is not null) continue;
            if (string.Equals(candidate.Text, oldItem.Text, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        var oldStem = StripCountSuffix(oldItem.Text);
        foreach (var candidate in newItems)
        {
            if (candidate.IsSeparator || candidate.Key is not null) continue;
            if (string.Equals(StripCountSuffix(candidate.Text), oldStem, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Strips a trailing "(n)" count — optional preceding whitespace, digits
    /// only inside the parentheses — so "Pending Approvals (3)" and
    /// "Pending Approvals (5)" compare equal. Anything else returns the input
    /// trimmed of trailing whitespace.
    /// </summary>
    internal static string StripCountSuffix(string text)
    {
        var span = text.AsSpan().TrimEnd();
        if (span.Length < 3 || span[^1] != ')') return span.ToString();

        var open = span.LastIndexOf('(');
        if (open < 0) return span.ToString();

        var digits = span[(open + 1)..^1];
        if (digits.IsEmpty) return span.ToString();
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c)) return span.ToString();
        }

        return span[..open].TrimEnd().ToString();
    }
}
