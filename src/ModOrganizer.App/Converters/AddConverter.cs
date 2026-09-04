using System.Globalization;
using System.Windows.Data;

namespace ModOrganizer.App.Converters;

/// <summary>
/// Adds a constant (the converter parameter) to a numeric binding. Used to turn the
/// card size into the virtualizing panel's slot size, which has to include the card
/// margins or the layout drifts out of the viewport.
/// </summary>
public sealed class AddConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var baseValue = value switch
        {
            double d => d,
            int i => i,
            long l => l,
            _ => 0d
        };

        var offset = parameter is null
            ? 0d
            : double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var p)
                ? p
                : 0d;

        return baseValue + offset;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
