using System.Globalization;
using System.Windows.Data;

namespace ModOrganizer.App.Converters;

public sealed class IntGreaterEqualConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (!int.TryParse(parameter?.ToString(), out var threshold)) return false;
        int v = value switch
        {
            int i => i,
            long l => (int)l,
            _ => 0
        };
        return v >= threshold;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
