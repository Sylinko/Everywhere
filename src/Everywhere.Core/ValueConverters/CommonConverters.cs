using System.Globalization;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Everywhere.Common;
using ZLinq;

namespace Everywhere.ValueConverters;

public static class CommonConverters
{
    public static IValueConverter ObjectToString { get; } = new FuncValueConverter<object?, string?>(convert: x => x?.ToString());

    public static IValueConverter TypeEquals { get; } = new FuncValueConverter<object?, object?, bool>(
        convert: (x, parameter) => x?.GetType() == parameter as Type
    );

    public new static IValueConverter GetType { get; } = new FuncValueConverter<object?, object?>(
        convert: x => x?.GetType()
    );

    public static IValueConverter StringToUri { get; } = new BidirectionalFuncValueConverter<string?, Uri?>(
        convert: (x, _) => Uri.TryCreate(x, UriKind.RelativeOrAbsolute, out var uri) ? uri : null,
        convertBack: (x, _) => x?.ToString()
    );

    public static IValueConverter ColorToBrush { get; } = new FuncValueConverter<Color, SolidColorBrush>(
        convert: color => new SolidColorBrush(color)
    );

    public static IValueConverter DateTimeOffsetToString { get; } = new BidirectionalFuncValueConverter<DateTimeOffset, string>(
        convert: (x, p) => x.DateTime.ToLocalTime().ToString(p?.ToString()),
        convertBack: (x, p) => DateTimeOffset.ParseExact(x, p?.ToString() ?? "o", null)
    );

    public static IValueConverter TimeSpanToSeconds { get; } = new FuncValueConverter<TimeSpan, string?, string>(
        convert: (x, format) => x.TotalSeconds.ToString(format));

    public static IValueConverter FullPathToFileName { get; } = new FuncValueConverter<string, string?>(
        convert: x => Path.GetFileName(x) is { Length: > 0 } fileName ? fileName : x // return original if no file name found (e.g. Path root)
    );

    /// <summary>
    /// Converts an Enum Type to its values array.
    /// </summary>
    public static IValueConverter EnumTypeToValues { get; } = new FuncValueConverter<Type?, Type?, Array?>(
        convert: (x, parameter) =>
        {
            var type = x ?? parameter;
            return type?.IsEnum is true ? Enum.GetValues(type) : null;
        });

    /// <summary>
    /// Converts an Enum value to its localized string representation.
    /// </summary>
    public static IValueConverter EnumI18N { get; } = new FuncValueConverter<object?, string?>(
        convert: x =>
        {
            if (x?.GetType() is not { IsEnum: true } type) return null;

            var enumName = Enum.GetName(type, x);
            if (enumName is null) return null;

            var key = type.GetField(enumName)?.GetCustomAttribute<DynamicResourceKeyAttribute>()?.HeaderKey ?? $"{type.Name}_{enumName}";
            return DynamicResourceKey.Resolve(key);
        });

    public static IValueConverter IndexFromContainer { get; } = new FuncValueConverter<object?, int>(
        convert: x =>
        {
            if (x is not Control itemContainer) return -1;
            var itemsControl = ItemsControl.ItemsControlFromItemContainer(itemContainer);
            return itemsControl?.IndexFromContainer(itemContainer) ?? -1;
        });

    public static IMultiValueConverter DefaultMultiValue { get; } = new DefaultMultiValueConverter();

    public static IMultiValueConverter AllEquals { get; } = new AllEqualsConverter();

    /// <summary>
    /// Returns the first non-null and non-UnsetValue value from the input values.
    /// </summary>
    public static IMultiValueConverter FirstNotNull { get; } = new FirstNonNullConverter();

    private class DefaultMultiValueConverter : IMultiValueConverter
    {
        private readonly DefaultValueConverter _defaultValueConverter = new();

        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            var value = values.AsValueEnumerable().FirstOrDefault(v => v != AvaloniaProperty.UnsetValue) ?? parameter;
            return value switch
            {
                null => null,
                Color color when typeof(SolidColorBrush).IsAssignableTo(targetType) => new SolidColorBrush(color),
                SerializableColor color when typeof(SolidColorBrush).IsAssignableTo(targetType) => new SolidColorBrush(color),
                _ => _defaultValueConverter.Convert(value, targetType, null, culture)
            };
        }
    }

    private class AllEqualsConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values.AsValueEnumerable().Any(v => v == AvaloniaProperty.UnsetValue)) return AvaloniaProperty.UnsetValue;
            var firstValue = values[0];
            return values.AsValueEnumerable().All(v => Equals(v, firstValue));
        }
    }

    private class FirstNonNullConverter : IMultiValueConverter
    {
        public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            return values.AsValueEnumerable().OfType<object>().FirstOrDefault(value => value != AvaloniaProperty.UnsetValue);
        }
    }
}