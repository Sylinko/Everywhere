using System.Globalization;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

/// <summary>
/// Converts numeric bindings into layout thicknesses.
/// </summary>
public static class ThicknessConverters
{
    /// <summary>
    /// Creates zero thickness from no values, uniform thickness from one, horizontal/vertical
    /// thickness from two, or left/top/right/bottom thickness from four values.
    /// Unsupported counts and unresolved or non-double values return UnsetValue.
    /// </summary>
    public static IMultiValueConverter FromValues { get; } = new FromValuesConverter();

    private sealed class FromValuesConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            return values switch
            {
                [] => default(Thickness),
                [double uniform] => new Thickness(uniform),
                [double horizontal, double vertical] => new Thickness(horizontal, vertical),
                [double left, double top, double right, double bottom] => new Thickness(left, top, right, bottom),
                _ => AvaloniaProperty.UnsetValue
            };
        }
    }
}