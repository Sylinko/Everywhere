using System.ComponentModel;
using System.Globalization;
using System.Reflection;

namespace Everywhere.Configuration;

/// <summary>
/// A TypeConverter for Enum that falls back to the first value of the enum if conversion fails.
/// </summary>
public sealed class FallbackEnumConverter(Type type) : EnumConverter(type)
{
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        try
        {
            return base.ConvertFrom(context, culture, value);
        }
        catch (FormatException)
        {
            // If the EnumType has attribute [DefaultValue], use that value.
            if (EnumType.GetCustomAttribute<DefaultValueAttribute>() is { Value: { } defaultValue })
            {
                try
                {
                    return Convert.ChangeType(defaultValue, EnumType, CultureInfo.InvariantCulture);
                }
                catch
                {
                    // If conversion fails, do nothing
                }
            }

            return Enum.GetValues(EnumType).GetValue(0);
        }
    }
}