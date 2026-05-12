using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AzureTray.Plugin.Contracts;

namespace AzureTray.Notifications;

// Maps NotificationSeverity to the brush used for the notification's
// accent stripe + title text. Colours mirror the theme palette so the
// notification reads the same as the rest of the dark UI.
internal sealed class SeverityToBrushConverter : IValueConverter
{
    // Accent (blue) for Update — matches Color.Accent in Theme.xaml.
    private static readonly SolidColorBrush UpdateBrush = new(System.Windows.Media.Color.FromRgb(0x1F, 0x6F, 0xEB));
    // Amber for Warning.
    private static readonly SolidColorBrush WarningBrush = new(System.Windows.Media.Color.FromRgb(0xDC, 0xB6, 0x7A));
    // Coral-red for Error.
    private static readonly SolidColorBrush ErrorBrush = new(System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
    // Neutral muted grey for Info — blends with the surface, looks passive.
    private static readonly SolidColorBrush InfoBrush = new(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x8A));

    static SeverityToBrushConverter()
    {
        UpdateBrush.Freeze();
        WarningBrush.Freeze();
        ErrorBrush.Freeze();
        InfoBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            NotificationSeverity.Update => UpdateBrush,
            NotificationSeverity.Warning => WarningBrush,
            NotificationSeverity.Error => ErrorBrush,
            _ => InfoBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
