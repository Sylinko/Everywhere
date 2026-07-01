using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Everywhere.Configuration;

/// <summary>
/// This class is used to wrap a customizable property.
/// </summary>
/// <typeparam name="T"></typeparam>
[JsonConverter(typeof(CustomizableJsonConverterFactory))]
public partial class Customizable<T> : ObservableObject where T : notnull
{
    public required T DefaultValue
    {
        get;
        set
        {
            if (_isDefaultValueReadonly) return;
            if (!SetProperty(ref field, value)) return;

            OnPropertyChanged(nameof(ActualValue));
            OnPropertyChanged(nameof(BindableValue));
        }
    }

    /// <summary>
    /// If T is a value type, T? will not be a nullable type.
    /// So we can only use object? to allow null values.
    /// </summary>
    [IgnoreDataMember]
    public object? CustomValue
    {
        get;
        set
        {
            value = ConvertValue(value);
            if (!SetProperty(ref field, value)) return;

            OnPropertyChanged(nameof(ActualValue));
            OnPropertyChanged(nameof(BindableValue));
        }
    }

    [JsonIgnore]
    public bool IsCustomValueSet => CustomValue is T;

    [JsonIgnore]
    public T ActualValue => CustomValue is T value ? value : DefaultValue;

    [JsonIgnore]
    public T? BindableValue
    {
        get => CustomValue is null ? typeof(T).IsClass ? default : DefaultValue : (T?)CustomValue;
        set
        {
            if (value is string { Length: 0 }) value = default; // Treat empty string as null for string types

            if (EqualityComparer<T>.Default.Equals(ActualValue, value)) return;

            CustomValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Indicates whether the default value is read-only. Which means the CustomValue cannot be set after construction.
    /// </summary>
    private readonly bool _isDefaultValueReadonly;

    [JsonConstructor]
    public Customizable() { }

    /// <summary>
    /// Initializes a new instance of the <see cref="Customizable{T}"/> class.
    /// </summary>
    /// <param name="defaultValue"></param>
    /// <param name="customValue"></param>
    /// <param name="isDefaultValueReadonly"></param>
    [SetsRequiredMembers]
    public Customizable(T defaultValue, T? customValue = default, bool isDefaultValueReadonly = false) : this()
    {
        DefaultValue = defaultValue;
        CustomValue = customValue;
        _isDefaultValueReadonly = isDefaultValueReadonly;
    }

    [RelayCommand]
    [property: JsonIgnore]
    [property: IgnoreDataMember]
    private void Reset()
    {
        CustomValue = null;
    }

    public static implicit operator Customizable<T>(T value) => new() { DefaultValue = value };

    public static implicit operator T(Customizable<T> customizable) => customizable.ActualValue;

    public override string? ToString() => ActualValue.ToString();

    /// <summary>
    /// Converts an object to type T, or returns default if conversion fails.
    /// Useful for deserialization scenarios.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    private static object? ConvertValue(object? value)
    {
        switch (value)
        {
            case null:
            {
                return null;
            }
            case T tValue:
            {
                return tValue;
            }
            default:
            {
                try
                {
                    // When setting from JSON deserialization, the value may be of a different type.
                    // Try to convert it to the correct type.
                    if (typeof(T).IsEnum)
                    {
                        // Enum is serialized as string (int or name)
                        if (value is string enumString)
                        {
                            if (int.TryParse(enumString, out var enumValue))
                            {
                                return (T)Enum.ToObject(typeof(T), enumValue);
                            }

                            return (T)Enum.Parse(typeof(T), enumString, true);
                        }

                        return (T)Enum.ToObject(typeof(T), Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    }

                    if (value is JsonElement element)
                    {
                        return element.Deserialize<T>();
                    }

                    return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
                }
                catch
                {
                    return null;
                }
            }
        }
    }

    /// <summary>
    /// Provides JSON serialization support for the Customizable{T} class.
    /// if _isDefaultValueReadonly is true, only serialize CustomValue.
    /// </summary>
    public sealed class JsonConverter : JsonConverter<Customizable<T>>
    {
        public override Customizable<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("Expected StartObject token");
            }

            T? defaultValue = default;
            object? customValue = null;

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Expected PropertyName token");
                }

                var propertyName = reader.GetString();
                reader.Read();

                switch (propertyName)
                {
                    case nameof(DefaultValue):
                        defaultValue = JsonSerializer.Deserialize<T>(ref reader, options);
                        break;
                    case nameof(CustomValue):
                        customValue = JsonSerializer.Deserialize<object>(ref reader, options);
                        break;
                }
            }

            if (defaultValue is null)
            {
                throw new NotSupportedException("Customizable<T> must have a DefaultValue when deserialized from JSON.");
            }

            var customizable = new Customizable<T>
            {
                DefaultValue = defaultValue,
                CustomValue = ConvertValue(customValue)
            };

            return customizable;
        }

        public override void Write(Utf8JsonWriter writer, Customizable<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();

            if (!value._isDefaultValueReadonly)
            {
                writer.WritePropertyName(nameof(DefaultValue));
                JsonSerializer.Serialize(writer, value.DefaultValue, options);
            }

            writer.WritePropertyName(nameof(CustomValue));
            JsonSerializer.Serialize(writer, value.CustomValue, options);

            writer.WriteEndObject();
        }
    }
}

public sealed class CustomizableJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
    {
        if (!typeToConvert.IsGenericType) return false;

        return typeToConvert.GetGenericTypeDefinition() == typeof(Customizable<>);
    }

    public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var itemType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(Customizable<>.JsonConverter).MakeGenericType(itemType);
        return (JsonConverter?)Activator.CreateInstance(converterType);
    }
}