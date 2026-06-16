using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

/// <summary>
/// Resolves a <see cref="DynamicLocaleKeyAttribute"/> to the actual resource.
/// </summary>
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.PublicFields |
    DynamicallyAccessedMemberTypes.PublicProperties)]
public class DynamicLocaleKeyConverter : IValueConverter
{
    public static DynamicLocaleKeyConverter Shared { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // 1. value is an object with DynamicLocaleKeyAttribute on class
        // 2. value is an enum with DynamicLocaleKeyAttribute on field

        if (value is null) return null;

        var type = value.GetType();
        DynamicLocaleKeyAttribute? attribute;
        if (type.IsEnum)
        {
            attribute = type.GetField(value.ToString() ?? string.Empty)?.GetCustomAttributes<DynamicLocaleKeyAttribute>(true).FirstOrDefault();
        }
        else
        {
            attribute = type.GetCustomAttributes<DynamicLocaleKeyAttribute>(true).FirstOrDefault();
        }

        return attribute is null ? null : new DynamicLocaleKey(attribute.HeaderKey);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}