using System;
using System.Globalization;
using System.Windows.Data;
using Serilog.Events;

namespace AzureTray.Logging;

// Three-character severity glyph for the log row's gutter. Pairs with
// LevelToBrushConverter — the glyph reads the same shape ("ERR") while
// the brush gives it the colour ("red"), so a colour-blind user still
// has the text affordance and a casual scanner still has the colour.
internal sealed class LevelToGlyphConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            LogEventLevel.Fatal => "FTL",
            LogEventLevel.Error => "ERR",
            LogEventLevel.Warning => "WRN",
            LogEventLevel.Information => "INF",
            LogEventLevel.Debug => "DBG",
            LogEventLevel.Verbose => "VRB",
            _ => "—",
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
