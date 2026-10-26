using System.Globalization;
using Avalonia.Data.Converters;

namespace Coppice.Gui.Converters;

/// <summary>Compares a value with the converter parameter. Used for active-page classes and visibility.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
