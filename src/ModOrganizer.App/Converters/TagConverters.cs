using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using ModOrganizer.App.ViewModels;
using System.Windows;

namespace ModOrganizer.App.Converters;

/// <summary>Enables a panel only while something is selected.</summary>
public sealed class NotNullToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Negates a bool - "enabled while not busy".</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not bool b || !b;
}

/// <summary>Visible when the bound value is set - used for the "is viewing a mod" dot.</summary>
public sealed class NotNullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Turns a "#RRGGBB" string into a frozen brush, for the colour-picker swatches.</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hex = value?.ToString();
        return string.IsNullOrWhiteSpace(hex) ? Brushes.Transparent : TagBrushes.Frozen(hex!);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Brush for a tag chip on a gallery card. ModTagRef lives in Core, which has no WPF
/// reference, so the colour is resolved here instead of on the model.
/// </summary>
public sealed class TagRefToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is ModOrganizer.Core.Queries.ModTagRef tag
            ? TagBrushes.Solid(tag.ColorHex, tag.Name)
            : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
