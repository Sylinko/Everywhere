using System.Globalization;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

/// <summary>
/// Converts a boolean (true, false) to emoji (✅, ❌).
/// </summary>
public class BooleanToEmojiConverter : IValueConverter
{
    public static BooleanToEmojiConverter Shared { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => System.Convert.ToBoolean(value) ? "✅" : "❌";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}