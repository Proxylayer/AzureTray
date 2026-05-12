using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Serilog.Events;

namespace AzureTray.Logging;

internal sealed class LevelToBrushConverter : IValueConverter
{
    // Colors mirror the theme's status palette (Status.Error / Status.Warning
    // / Status.Success) so the log viewer reads the same on dark surfaces as
    // the rest of the app. Update Resources/Theme.xaml in lockstep.
    private static readonly SolidColorBrush FatalBrush = new(System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly SolidColorBrush ErrorBrush = new(System.Windows.Media.Color.FromRgb(0xF4, 0x87, 0x71));
    private static readonly SolidColorBrush WarningBrush = new(System.Windows.Media.Color.FromRgb(0xDC, 0xB6, 0x7A));
    private static readonly SolidColorBrush InfoBrush = new(System.Windows.Media.Color.FromRgb(0xE4, 0xE4, 0xE4));
    private static readonly SolidColorBrush DebugBrush = new(System.Windows.Media.Color.FromRgb(0xB5, 0xB5, 0xB5));
    private static readonly SolidColorBrush VerboseBrush = new(System.Windows.Media.Color.FromRgb(0x85, 0x85, 0x85));

    static LevelToBrushConverter()
    {
        FatalBrush.Freeze();
        ErrorBrush.Freeze();
        WarningBrush.Freeze();
        InfoBrush.Freeze();
        DebugBrush.Freeze();
        VerboseBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            LogEventLevel.Fatal => FatalBrush,
            LogEventLevel.Error => ErrorBrush,
            LogEventLevel.Warning => WarningBrush,
            LogEventLevel.Information => InfoBrush,
            LogEventLevel.Debug => DebugBrush,
            LogEventLevel.Verbose => VerboseBrush,
            _ => InfoBrush,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
