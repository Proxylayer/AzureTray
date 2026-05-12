using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AzureTray.Shell;

// IValueConverter: int → Visibility. Use to collapse a panel when a bound
// ObservableCollection is empty. Pass an int via Visibility="{Binding Foo.Count, ...}".
[ValueConversion(typeof(int), typeof(Visibility))]
internal sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int n && n > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
