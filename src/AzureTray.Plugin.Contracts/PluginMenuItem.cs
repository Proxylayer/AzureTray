using System;
using System.Collections.Generic;

namespace AzureTray.Plugin.Contracts;

/// <summary>
/// A single entry the plugin contributes to the host's tray context menu.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Leaf item:</strong> set <see cref="Invoke"/> only.<br/>
/// <strong>Submenu:</strong> set <see cref="Children"/> only (Invoke is ignored).<br/>
/// <strong>Searchable submenu:</strong> set <see cref="SearchProvider"/>; Children
/// is ignored and the initial items come from <c>SearchProvider("")</c>.<br/>
/// <strong>Disabled label:</strong> set neither Invoke nor Children.<br/>
/// <strong>Separator:</strong> use the static <see cref="Separator"/> instance.
/// </para>
/// <para>
/// The host rebuilds the menu on every right-click, so
/// <see cref="ITrayPlugin.GetMenuItems"/> may return fresh state freely.
/// Keep it fast — it runs on the UI thread.
/// </para>
/// <para>
/// When <see cref="IsBusy"/> is <c>true</c>, the host swaps the leading glyph
/// for a rotating spinner and animates it in place without a full menu rebuild.
/// Set this while a background request is in flight and fire
/// <see cref="IMenuChangeNotifier.MenuChanged"/> once at the transition.
/// </para>
/// <para>
/// When <see cref="KeepMenuOpen"/> is <c>true</c>, clicking still fires
/// <see cref="Invoke"/> but does not dismiss the menu. Use for refresh-style
/// actions where the user expects to see the result land in the visible menu.
/// </para>
/// <para>
/// When <see cref="IsFavorite"/> is non-null the host renders a star at the
/// right edge of the row (☆ for <c>false</c>, ★ for <c>true</c>). Clicking the
/// star fires <see cref="OnToggleFavorite"/> and flips the glyph in place
/// without dismissing the menu or triggering the row's primary
/// <see cref="Invoke"/>/<see cref="Children"/> action. Leave it <c>null</c> on
/// rows that aren't favoritable so no star is shown.
/// </para>
/// </remarks>
public sealed record PluginMenuItem(
    string Text,
    Action? Invoke = null,
    bool IsEnabled = true,
    IReadOnlyList<PluginMenuItem>? Children = null,
    bool IsSeparator = false,
    bool IsBusy = false,
    bool KeepMenuOpen = false,
    string? Icon = null,
    Func<string, IReadOnlyList<PluginMenuItem>>? SearchProvider = null,
    string? SearchPlaceholder = null,
    bool? IsFavorite = null,
    Action? OnToggleFavorite = null)
{
    /// <summary>A pre-built horizontal divider. Use instead of constructing manually.</summary>
    public static PluginMenuItem Separator { get; } = new(string.Empty, IsSeparator: true);

    /// <summary>
    /// <c>true</c> when this item has children or a search provider.
    /// Convenience for XAML data binding (chevron visibility).
    /// </summary>
    public bool HasChildren => Children is { Count: > 0 } || SearchProvider is not null;

    /// <summary>
    /// <c>true</c> when this row should display a favorite star. Convenience for
    /// XAML data binding (star visibility); mirrors <see cref="IsFavorite"/>
    /// having a value.
    /// </summary>
    public bool ShowFavorite => IsFavorite.HasValue;
}
