using System.Globalization;
using Avalonia.Data.Converters;
using Everywhere.Utilities;

namespace Everywhere.ValueConverters;

public sealed class HumanizeBytesValueConverter : IValueConverter
{
    public static HumanizeBytesValueConverter Shared { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            long l => FileUtilities.HumanizeBytes(l),
            int i => FileUtilities.HumanizeBytes(i),
            _ => "0 byte"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}