using System.Buffers;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Everywhere.Configuration.Engine;

internal static class DictionaryKeyReader
{
    private static readonly ConcurrentDictionary<Type, Func<string, object?>> Readers = [];

    public static Func<string, object?> Create(Type keyType) =>
        keyType == typeof(string) ?
            static keyText => keyText :
            Readers.GetOrAdd(keyType, CreateCore);

    private static Func<string, object?> CreateCore(Type keyType)
    {
        var readerType = typeof(DictionaryKeyReader<>).MakeGenericType(keyType);
        var method = readerType.GetMethod(
                nameof(DictionaryKeyReader<int>.ReadObject),
                BindingFlags.Public | BindingFlags.Static) ??
            throw new MissingMethodException(readerType.FullName, nameof(DictionaryKeyReader<int>.ReadObject));

        return (Func<string, object?>)method.CreateDelegate(typeof(Func<string, object?>));
    }
}

/// <summary>
/// Bridges JSON object member names into STJ's dictionary-key converter path.
/// </summary>
/// <remarks>
/// <para>
/// Dictionary keys are not ordinary JSON values. For example, the member name
/// <c>"1"</c> should bind to <c>Dictionary&lt;int, TValue&gt;</c> by reading a
/// JSON <see cref="JsonTokenType.PropertyName"/>, while wrapping that text as
/// a JSON string value would exercise a different converter path. The public
/// STJ surface exposes <see cref="JsonConverter{T}.ReadAsPropertyName"/>, but
/// it still requires a reader positioned on a property name and the converter
/// instance used by <c>JsonDictionaryConverter</c> is the internal
/// <c>JsonTypeInfo&lt;TKey&gt;.EffectiveConverter</c>, not always the public
/// converter object returned directly from options.
/// </para>
/// <para>
/// This helper intentionally mirrors the narrow part of STJ's dictionary
/// reader that converts the key name: create a minimal object with the key as
/// a property name, advance the reader to that property, then call the
/// converter's internal <c>ReadAsPropertyNameCore</c>. If a future STJ version
/// renames or changes these internal members, the break should be contained to
/// this helper; replace the unsafe accessors with the new official API if one
/// exists, or fall back to deserializing a one-entry dictionary through STJ's
/// public serializer to preserve dictionary-key semantics.
/// </para>
/// </remarks>
internal static class DictionaryKeyReader<TKey>
{
    public static object? ReadObject(string keyText) =>
        Read(keyText, SettingsEngineJson.SerializerOptions);

    public static TKey Read(string keyText, JsonSerializerOptions options)
    {
        if (typeof(TKey) == typeof(string))
        {
            return (TKey)(object)keyText;
        }

        var converter = GetDictionaryKeyConverter(options);
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = options.Encoder }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(keyText);
            writer.WriteNullValue();
            writer.WriteEndObject();
            writer.Flush();
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan, isFinalBlock: true, state: default);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject ||
            !reader.Read() || reader.TokenType != JsonTokenType.PropertyName)
        {
            throw new JsonException("Failed to create a JSON object member name reader.");
        }

        return ReadAsPropertyNameCore(converter, ref reader, converter.Type, options);
    }

    private static JsonConverter<TKey> GetDictionaryKeyConverter(JsonSerializerOptions options)
    {
        try
        {
            var typeInfo = (JsonTypeInfo<TKey>)options.GetTypeInfo(typeof(TKey));
            return GetEffectiveConverter(typeInfo);
        }
        catch (NotSupportedException)
        {
            // Test-only and plugin-provided settings types may be patched by
            // descriptors without being rooted in the generated serializer
            // context. STJ dictionary converters use KeyTypeInfo when it is
            // available. Calling JsonSerializerOptions.GetConverter() would
            // ask the same source-generated resolver for metadata and throw
            // again, so scan the explicitly registered converters ourselves
            // before falling back to STJ's default converter table.
            return GetFallbackConverter(options);
        }
    }

    private static JsonConverter<TKey> GetFallbackConverter(JsonSerializerOptions options)
    {
        foreach (var converter in options.Converters)
        {
            if (converter.CanConvert(typeof(TKey)))
            {
                return ExpandConverter(converter, options);
            }
        }

        return ExpandConverter(JsonSerializerOptions.Default.GetConverter(typeof(TKey)), options);
    }

    private static JsonConverter<TKey> ExpandConverter(JsonConverter converter, JsonSerializerOptions options)
    {
        if (converter is JsonConverterFactory factory)
        {
            converter = factory.CreateConverter(typeof(TKey), options) ??
                throw new NotSupportedException(
                    $"The JSON converter factory '{factory.GetType()}' returned null for dictionary key type '{typeof(TKey)}'.");
        }

        return (JsonConverter<TKey>)converter;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_EffectiveConverter")]
    private static extern JsonConverter<TKey> GetEffectiveConverter(JsonTypeInfo<TKey> typeInfo);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ReadAsPropertyNameCore")]
    private static extern TKey ReadAsPropertyNameCore(
        JsonConverter<TKey> converter,
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options);
}
