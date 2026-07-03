using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using Avalonia.Data.Converters;

namespace Everywhere.ValueConverters;

/// <summary>
/// Resolves a <see cref="DynamicLocaleKeyAttribute"/> to a dynamic locale key.
/// </summary>
/// <remarks>
/// This converter is used in item templates and hot UI paths, so reflected attribute lookup is
/// cached per concrete type and enum field. Missing attributes are cached too to avoid repeating
/// the same failed lookup for unannotated values.
/// </remarks>
[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors |
    DynamicallyAccessedMemberTypes.PublicFields |
    DynamicallyAccessedMemberTypes.PublicProperties)]
public sealed class DynamicLocaleKeyConverter : IValueConverter
{
    public static DynamicLocaleKeyConverter Shared { get; } = new();

    private static readonly Dictionary<(Type Type, string Name), AttributeCacheEntry> EnumAttributeCache = new();
    private static readonly Dictionary<Type, AttributeCacheEntry> TypeAttributeCache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;

        var type = value.GetType();
        var cacheEntry = type.IsEnum ?
            GetOrAdd(EnumAttributeCache, (type, value.ToString() ?? string.Empty), static key => GetEnumAttribute(key.Item1, key.Item2)) :
            GetOrAdd(TypeAttributeCache, type, static t => GetTypeAttribute(t));

        return cacheEntry.HeaderKey is null ? null : new DynamicLocaleKey(cacheEntry.HeaderKey);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static AttributeCacheEntry GetEnumAttribute(Type type, string name)
    {
        var attribute = type.GetField(name)?.GetCustomAttribute<DynamicLocaleKeyAttribute>(true);
        return new AttributeCacheEntry(attribute?.HeaderKey);
    }

    private static AttributeCacheEntry GetTypeAttribute(Type type)
    {
        var attribute = type.GetCustomAttribute<DynamicLocaleKeyAttribute>(true);
        return new AttributeCacheEntry(attribute?.HeaderKey);
    }

    private static AttributeCacheEntry GetOrAdd<TKey>(
        Dictionary<TKey, AttributeCacheEntry> cache,
        TKey key,
        Func<TKey, AttributeCacheEntry> valueFactory) where TKey : notnull
    {
        if (cache.TryGetValue(key, out var value))
        {
            return value;
        }

        value = valueFactory(key);
        cache.Add(key, value);
        return value;
    }

    private readonly record struct AttributeCacheEntry(string? HeaderKey);
}