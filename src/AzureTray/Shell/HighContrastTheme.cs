using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

// Disambiguate from the System.Drawing types (in scope because
// UseWindowsForms is enabled at the project level for the tray NotifyIcon).
using Color = System.Windows.Media.Color;
using SystemColors = System.Windows.SystemColors;

namespace AzureTray.Shell;

/// <summary>
/// Builds the resource dictionary that remaps the app's <c>Brush.*</c> theme
/// tokens (Resources/Theme.xaml) onto the user's Windows High Contrast
/// palette. High Contrast mode intentionally overrides the app's dark
/// aesthetic: the goal is correct system-color mapping and legibility in
/// whichever HC theme the user picked, not a pixel-perfect HC redesign.
///
/// The mapping rules:
///  - surfaces          → Window          (backgrounds)
///  - text              → WindowText / GrayText (muted)
///  - selection fills   → Highlight, with HighlightText for text on them
///    (Brush.Text.Inverse is used by the templates exactly as
///    "text on the accent fill", so it maps to HighlightText)
///  - borders           → WindowFrame, WindowText when emphasised
///  - keyboard focus    → WindowText (the HC convention for focus rectangles)
///  - links             → HotTrack
///
/// Brushes are created UNFROZEN on purpose: one theme storyboard animates a
/// foreground brush's Color in place (TrayMenuWindow's busy spinner), and
/// animating a frozen brush throws.
/// </summary>
internal static class HighContrastTheme
{
    /// <summary>
    /// The brush keys the High Contrast dictionary overrides, with the system
    /// color each maps to. Exposed (rather than inlined into
    /// <see cref="CreateDictionary"/>) as the pure, testable seam.
    /// </summary>
    internal static IReadOnlyDictionary<string, Color> MapBrushKeys() => new Dictionary<string, Color>
    {
        // Surfaces: every layering tone collapses onto the HC window color.
        ["Brush.Surface.Base"] = SystemColors.WindowColor,
        ["Brush.Surface.Panel"] = SystemColors.WindowColor,
        ["Brush.Surface.Card"] = SystemColors.WindowColor,
        ["Brush.Surface.Input"] = SystemColors.WindowColor,
        // Hover keeps the window fill (text must stay WindowText-legible);
        // hover feedback in HC comes from the border-brush triggers instead.
        ["Brush.Surface.Hover"] = SystemColors.WindowColor,
        ["Brush.Surface.Pressed"] = SystemColors.WindowColor,

        ["Brush.Border.Subtle"] = SystemColors.WindowFrameColor,
        ["Brush.Border.Default"] = SystemColors.WindowFrameColor,
        ["Brush.Border.Strong"] = SystemColors.WindowTextColor,

        ["Brush.Text.Primary"] = SystemColors.WindowTextColor,
        ["Brush.Text.Secondary"] = SystemColors.WindowTextColor,
        ["Brush.Text.Muted"] = SystemColors.GrayTextColor,
        // "Text on the accent fill" — pairs with Brush.Accent = Highlight.
        ["Brush.Text.Inverse"] = SystemColors.HighlightTextColor,
        ["Brush.Text.Link"] = SystemColors.HotTrackColor,

        ["Brush.Accent"] = SystemColors.HighlightColor,
        ["Brush.Accent.Hover"] = SystemColors.HighlightColor,
        ["Brush.Accent.Pressed"] = SystemColors.HighlightColor,

        // Severity is still carried by the text itself; HC themes offer no
        // guaranteed-contrast warning/error colors, so map to WindowText.
        ["Brush.Status.Warning"] = SystemColors.WindowTextColor,
        ["Brush.Status.Error"] = SystemColors.WindowTextColor,
        ["Brush.Status.Success"] = SystemColors.WindowTextColor,

        ["Brush.Focus"] = SystemColors.WindowTextColor,

        ["Brush.Scroll.Thumb"] = SystemColors.ControlTextColor,
        ["Brush.Scroll.ThumbHover"] = SystemColors.HighlightColor,

        // The softened translucent selection set goes back to the real
        // system selection pair — HC selection must be fully visible.
        ["Brush.Selection.Soft"] = SystemColors.HighlightColor,
        ["Brush.Selection.SoftInactive"] = SystemColors.HighlightColor,
        ["Brush.Selection.SoftText"] = SystemColors.HighlightTextColor,
    };

    /// <summary>
    /// Materialises <see cref="MapBrushKeys"/> into a dictionary ready to be
    /// merged AFTER Theme.xaml (last merged dictionary wins for
    /// DynamicResource lookups).
    /// </summary>
    internal static ResourceDictionary CreateDictionary()
    {
        var dictionary = new ResourceDictionary();
        foreach (var (key, color) in MapBrushKeys())
        {
            // Deliberately not frozen — see the class remarks.
            dictionary[key] = new SolidColorBrush(color);
        }
        return dictionary;
    }
}
