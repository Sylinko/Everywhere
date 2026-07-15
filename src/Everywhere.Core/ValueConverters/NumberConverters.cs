using System.Globalization;
using System.Numerics;
using Avalonia.Data.Converters;
using Everywhere.Utilities;
using ZLinq;

namespace Everywhere.ValueConverters;

public abstract class NumberConverters<T> where T : struct, INumber<T>
{
    private static T ChangeType(object? value) => value is UnsetValueType ? default : (T)(Convert.ChangeType(value, typeof(T)) ?? default(T));

    public static IValueConverter IsZero { get; } = new BidirectionalFuncValueConverter<T, bool>(
        convert: static (x, _) => x == T.Zero,
        convertBack: static (_, _) => throw new NotSupportedException()
    );

    public static IValueConverter IsNotZero { get; } = new BidirectionalFuncValueConverter<T, bool>(
        convert: static (x, _) => x != T.Zero,
        convertBack: static (_, _) => throw new NotSupportedException()
    );

    public static IValueConverter Negate { get; } = new BidirectionalFuncValueConverter<T, T>(
        convert: static (x, _) => -x,
        convertBack: static (x, _) => -x
    );

    public static IValueConverter Plus { get; } = new BidirectionalFuncValueConverter<T, T>(
        convert: static (x, p) => x + ChangeType(p),
        convertBack: static (x, p) => x - ChangeType(p)
    );

    public static IMultiValueConverter Sum { get; } = new SumConverter();

    public static IValueConverter Multiply { get; } = new BidirectionalFuncValueConverter<T, T>(
        convert: static (x, p) => x * ChangeType(p),
        convertBack: static (x, p) => x / ChangeType(p)
    );

    public static IMultiValueConverter Product { get; } = new ProductConverter();

    public static IValueConverter NotGreaterThan { get; } = new BidirectionalFuncValueConverter<T, bool>(
        convert: static (x, p) => x <= ChangeType(p),
        convertBack: static (_, _) => throw new NotSupportedException()
    );

    public static IValueConverter GreaterThan { get; } = new BidirectionalFuncValueConverter<T, bool>(
        convert: static (x, p) => x > ChangeType(p),
        convertBack: static (_, _) => throw new NotSupportedException()
    );

    /// <summary>
    /// Multi-value converter that returns true if the first value is smaller than any subsequent values.
    /// </summary>
    public static IMultiValueConverter MultiSmallerThanAny { get; } = new FuncMultiValueConverter<T, bool>(
        // ReSharper disable PossibleMultipleEnumeration
        numbers => numbers.AsValueEnumerable().First() < numbers.AsValueEnumerable().Skip(1).Min()
        // ReSharper restore PossibleMultipleEnumeration
    );

    public static IValueConverter FromEnum { get; } = new FromEnumConverter();

    public static IValueConverter FromBoolean { get; } = new BidirectionalFuncValueConverter<bool, T>(
        convert: static (b, _) => b ? T.One : T.Zero,
        convertBack: static (n, _) => n != T.Zero
    );

    /// <summary>
    /// Converts a number into a human-readable string with appropriate suffixes (K for thousands, M for millions, B for billions).
    /// </summary>
    public static IValueConverter HumanizeNumber { get; } = new FuncValueConverter<T, string>(n => Humanizer.HumanizeNumber(n));

    /// <summary>
    /// Converts a byte size into a human-readable string with appropriate units.
    /// e.g., 1024 -> "1 KB", 1048576 -> "1 MB"
    /// </summary>
    public static IValueConverter HumanizeBytes { get; } = new FuncValueConverter<T, string>(n => Humanizer.HumanizeBytes(long.CreateChecked(n)));

    private sealed class SumConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            return values.AsValueEnumerable().Aggregate(T.Zero, (a, b) => a + ChangeType(b));
        }
    }

    private sealed class ProductConverter : IMultiValueConverter
    {
        public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        {
            return values.AsValueEnumerable().Aggregate(T.One, (a, b) => a * ChangeType(b));
        }
    }

    private sealed class FromEnumConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is null ? default : ChangeType(value);
        }

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            return value is null ? 0 : Enum.ToObject(targetType, System.Convert.ChangeType(value, TypeCode.Int64));
        }
    }
}

public sealed class Int32Converters : NumberConverters<int>;
public sealed class Int64Converters : NumberConverters<long>;
public sealed class DoubleConverters : NumberConverters<double>;
public sealed class SingleConverters : NumberConverters<float>;
public sealed class DecimalConverters : NumberConverters<decimal>;
