using System.Globalization;
using System.Windows.Data;

namespace ModOrganizer.App.Converters;

public sealed class ViewModeLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is bool b && b ? "Folder" : "Grid";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
