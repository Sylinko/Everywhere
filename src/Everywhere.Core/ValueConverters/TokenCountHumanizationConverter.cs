using System.Globalization;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

/// <summary>
/// Converts a token count (int) to a human-readable string format (e.g., "1.2K" for 1245, "3.6M" for 3,600,000).
/// </summary>
public class TokenCountHumanizationConverter : IValueConverter
{
    public static TokenCountHumanizationConverter Shared { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var count = System.Convert.ToInt64(value);
        return count switch
        {
            < 1_000 => count.ToString(culture),
            < 1_000_000 => (count / 1_000.0).ToString("0.#", culture) + "K",
            < 1_000_000_000 => (count / 1_000_000.0).ToString("0.#", culture) + "M",
            _ => (count / 1_000_000_000.0).ToString("0.#", culture) + "B"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}