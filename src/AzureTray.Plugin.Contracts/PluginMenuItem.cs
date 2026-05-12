using System;
using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

// A single entry the plugin contributes to the host's tray context menu.
//
// - Set Invoke for a clickable leaf item.
// - Set Children for a submenu; in that case Invoke is ignored.
// - Leave Invoke null and Children null to render a disabled header / label.
// - Use the static Separator instance to insert a horizontal divider.
//
// The host rebuilds the menu on every right-click, so GetMenuItems is allowed
// to return fresh state. Keep it fast — it runs on the UI thread.
//
// When IsBusy is true, the host swaps the leading glyph for a rotating spinner
// frame and animates it in place — no menu rebuild per frame, so the rest of
// the menu stays still. Plugins set this true while a background refresh is
// running and fire MenuChanged once at the transition.
//
// When KeepMenuOpen is true, clicking the item still fires Invoke but does
// NOT dismiss the menu chain. Use for refresh-style actions where the user
// expects to see the result land in the visible menu without reopening it.
public sealed record PluginMenuItem(
    string Text,
    Action? Invoke = null,
    bool IsEnabled = true,
    IReadOnlyList<PluginMenuItem>? Children = null,
    bool IsSeparator = false,
    bool IsBusy = false,
    bool KeepMenuOpen = false,
    string? Icon = null,
    // When SearchProvider is set, opening this submenu shows a search box
    // at the top of the flyout. As the user types, SearchProvider is
    // called and the items list refreshes. The initial items come from
    // SearchProvider("") — Children is ignored for searchable submenus.
    Func<string, IReadOnlyList<PluginMenuItem>>? SearchProvider = null,
    string? SearchPlaceholder = null)
{
    public static PluginMenuItem Separator { get; } = new(string.Empty, IsSeparator: true);

    // Convenience for XAML data binding so the host doesn't need a
    // null-or-empty-list converter for chevron visibility. A searchable
    // submenu counts as having children even when the initial Children
    // list is empty — opening it shows the search box + provider results.
    public bool HasChildren => Children is { Count: > 0 } || SearchProvider is not null;
}
