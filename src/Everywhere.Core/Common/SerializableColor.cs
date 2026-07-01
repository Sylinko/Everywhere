using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Everywhere.Common;

[Serializable]
[TypeConverter(typeof(SerializableColorTypeConverter))]
[JsonConverter(typeof(SerializableColorJsonConverter))]
public struct SerializableColor : IEquatable<SerializableColor>
{
    public byte A { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public static implicit operator SerializableColor(Color color) => new()
    {
        A = color.A,
        R = color.R,
        G = color.G,
        B = color.B
    };

    public static implicit operator Color(SerializableColor color) => Color.FromArgb(color.A, color.R, color.G, color.B);

    public bool Equals(SerializableColor other) => A == other.A && R == other.R && G == other.G && B == other.B;

    public override bool Equals(object? obj) => obj is SerializableColor other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(A, R, G, B);

    public static bool operator ==(SerializableColor left, SerializableColor right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(SerializableColor left, SerializableColor right)
    {
        return !(left == right);
    }

    public override string ToString()
    {
        return $"#{A:X2}{R:X2}{G:X2}{B:X2}";
    }
}

public sealed class SerializableColorTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string colorString)
        {
            if (colorString.IsNullOrWhiteSpace()) return default(SerializableColor);
            if (!Color.TryParse(colorString, out var color)) color = default;
            return (SerializableColor)color;
        }

        return base.ConvertFrom(context, culture, value);
    }
}

public sealed class SerializableColorJsonConverter : JsonConverter<SerializableColor>
{
    public override SerializableColor Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var colorString = reader.GetString();
        if (colorString.IsNullOrWhiteSpace()) return default;
        if (!Color.TryParse(colorString, out var color)) color = default;
        return color;
    }

    public override void Write(Utf8JsonWriter writer, SerializableColor value, JsonSerializerOptions options)
    {
        var color = (Color)value;
        var colorString = color.ToString();
        writer.WriteStringValue(colorString);
    }
}

public static class SerializableColorValueConverters
{
    public static IValueConverter ToColor { get; } = new FuncValueConverter<SerializableColor?, Color?>(color => color, color => color);

    public static IValueConverter FromColor { get; } = new FuncValueConverter<Color?, SerializableColor?>(color => color, color => color);
}