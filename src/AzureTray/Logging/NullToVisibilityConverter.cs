using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AzureTray.Logging;

// Collapses an element when its bound value is null. Used by the log
// viewer's per-row exception block so rows without an attached exception
// don't grow extra height.
internal sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
