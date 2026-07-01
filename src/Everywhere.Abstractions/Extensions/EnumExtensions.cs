using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.Serialization;

namespace Everywhere.Extensions;

public static class EnumExtensions
{
    public static string ToFriendlyString<T>(this T value) where T : struct, Enum
    {
        var member = value.GetType().GetMember(value.ToString()).FirstOrDefault();
        if (member == null) return value.ToString();
        var attribute = member.GetCustomAttributes(typeof(EnumMemberAttribute), false).FirstOrDefault() as EnumMemberAttribute;
        return attribute?.Value ?? value.ToString();
    }

    extension(string name)
    {
        public T ToEnum<T>() where T : struct, Enum
        {
            return (T)name.ToEnum(typeof(T));
        }

        public object ToEnum(Type enumType)
        {
            if (!enumType.IsEnum) throw new ArgumentException("Type must be an enum", nameof(enumType));

            foreach (var field in enumType.GetFields())
            {
                var attribute = field.GetCustomAttribute<EnumMemberAttribute>();
                if (attribute?.Value == name) return field.GetValue(null).NotNull();
            }

            return Enum.Parse(enumType, name);
        }

        public bool TryToEnum<T>([NotNullWhen(true)] out T? value) where T : struct, Enum
        {
            var result = name.TryToEnum(typeof(T), out var obj);
            value = (T?)obj;
            return result;
        }

        public bool TryToEnum(Type enumType, [NotNullWhen(true)] out object? value)
        {
            value = null;
            if (!enumType.IsEnum) return false;

            foreach (var field in enumType.GetFields())
            {
                var attribute = field.GetCustomAttribute<EnumMemberAttribute>();
                if (attribute?.Value == name)
                {
                    value = field.GetValue(null).NotNull();
                    return true;
                }
            }

            return Enum.TryParse(enumType, name, out value);
        }
    }

    /// <summary>
    /// Ensures that the enum value is defined in the enum type. If it is not, returns the specified fallback value (or default if not specified).
    /// </summary>
    /// <param name="value"></param>
    /// <param name="fallbackValue"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static T EnsureDefined<T>(this T value, T fallbackValue = default) where T : struct, Enum
    {
        return Enum.IsDefined(value) ? value : fallbackValue;
    }
}