using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AzureTray.Plugin.Contracts;

// Disambiguate from System.Drawing.Color (in scope because UseWindowsForms
// is enabled at the project level for the tray NotifyIcon).
using Color = System.Windows.Media.Color;

namespace AzureTray.Notifications;

// Maps NotificationSeverity to brushes for the notification card. Passes
// `ConverterParameter="fill"` to get the dark tinted background; no
// parameter (default) returns the bright accent for border + title text.
//
// Two-tone-per-severity approach: bright accent on a dark tinted fill,
// matching the stacked-card mockup where each card is unmistakably
// "the red one / the yellow one / the green one / the blue one" without
// shouting.
internal sealed class SeverityToBrushConverter : IValueConverter
{
    // --- Accent (border + title text + status glyph) -------------------
    // Info / Update — bright blue. Standard "informational" hue; Update
    // shares it (it's "info with an upload arrow") so the two cards read
    // as the same colour class.
    private static readonly SolidColorBrush InfoBrush = new(Color.FromRgb(0x3B, 0x82, 0xF6));
    private static readonly SolidColorBrush UpdateBrush = InfoBrush;
    // Success — bright green. "Success!" card in the reference mockup.
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x22, 0xC5, 0x5E));
    // Warning — saturated yellow. "Action Needed" card.
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0xFA, 0xCC, 0x15));
    // Error — saturated red. "Oops!" card.
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xEF, 0x44, 0x44));

    // --- Fill (card background) ----------------------------------------
    // Dark, desaturated versions of the accent hues. Approximate HSV
    // lightness ~18 % so they read as "the dark tinted variant" of the
    // accent without losing contrast against the title text on top.
    // Tweak these if a particular card looks washed out or too dark —
    // they only drive Border.Background.
    private static readonly SolidColorBrush InfoFillBrush = new(Color.FromRgb(0x16, 0x24, 0x42));
    private static readonly SolidColorBrush UpdateFillBrush = InfoFillBrush;
    private static readonly SolidColorBrush SuccessFillBrush = new(Color.FromRgb(0x10, 0x2E, 0x1A));
    private static readonly SolidColorBrush WarningFillBrush = new(Color.FromRgb(0x3D, 0x31, 0x0B));
    private static readonly SolidColorBrush ErrorFillBrush = new(Color.FromRgb(0x3B, 0x15, 0x15));

    static SeverityToBrushConverter()
    {
        InfoBrush.Freeze();
        // UpdateBrush is aliased to InfoBrush; freezing the underlying
        // instance also marks UpdateBrush frozen.
        SuccessBrush.Freeze();
        WarningBrush.Freeze();
        ErrorBrush.Freeze();

        InfoFillBrush.Freeze();
        SuccessFillBrush.Freeze();
        WarningFillBrush.Freeze();
        ErrorFillBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var wantFill = parameter is string s
            && string.Equals(s, "fill", StringComparison.OrdinalIgnoreCase);

        return value switch
        {
            NotificationSeverity.Success => wantFill ? SuccessFillBrush : SuccessBrush,
            NotificationSeverity.Update => wantFill ? UpdateFillBrush : UpdateBrush,
            NotificationSeverity.Warning => wantFill ? WarningFillBrush : WarningBrush,
            NotificationSeverity.Error => wantFill ? ErrorFillBrush : ErrorBrush,
            _ => wantFill ? InfoFillBrush : InfoBrush,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
