using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Persocord.Views;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        v is Visibility.Visible;
}

/// <summary>Shows an element only when its bound string has something in it.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is string s && !string.IsNullOrWhiteSpace(s)
            ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object v, Type t, object p, CultureInfo c) =>
        throw new NotSupportedException();
}
