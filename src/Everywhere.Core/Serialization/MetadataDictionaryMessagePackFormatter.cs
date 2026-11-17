using System.Text.Json;
using Everywhere.Chat;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

/// <summary>
/// MessagePack formatter for serializing and deserializing metadata dictionaries with heterogeneous value types.
/// </summary>
public sealed class MetadataDictionaryMessagePackFormatter : IMessagePackFormatter<MetadataDictionary>
{
    private static readonly Dictionary<Type, int> TypeCodes = new(8)
    {
        { typeof(string), 1 },
        { typeof(int), 2 },
        { typeof(long), 3 },
        { typeof(float), 4 },
        { typeof(double), 5 },
        { typeof(bool), 6 },
        { typeof(byte[]), 7 },
        { typeof(JsonElement), 8 },
    };

    private static readonly Dictionary<int, Type> CodeTypes = TypeCodes.ToDictionary(kv => kv.Value, kv => kv.Key);

    /// <summary>
    /// Detaches typed properties at the tool boundary so unsupported values fail immediately rather than during persistence.
    /// </summary>
    public static Dictionary<string, object?>? CaptureProperties(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is not { Count: > 0 }) return null;

        var captured = new Dictionary<string, object?>(source.Count);
        foreach (var (key, value) in source)
        {
            captured[key] = value switch
            {
                null => null,
                JsonElement json when json.ValueKind != JsonValueKind.Undefined => json.Clone(),
                byte[] bytes => bytes.ToArray(),
                string or int or long or float or double or bool => value,
                _ => JsonSerializer.SerializeToElement(value, value.GetType())
            };
        }

        return captured;
    }

    public static void Serialize(
        ref MessagePackWriter writer,
        IReadOnlyDictionary<string, object?>? value,
        MessagePackSerializerOptions options)
    {
        writer.WriteMapHeader(value?.Count ?? 0);
        if (value is null) return;

        foreach (var (key, val) in value)
        {
            SerializeEntry(ref writer, key, val, options);
        }
    }

    public static void Serialize(
        ref MessagePackWriter writer,
        MetadataDictionary value,
        MessagePackSerializerOptions options)
    {
        writer.WriteMapHeader(value.Count);
        foreach (var (key, val) in value)
        {
            SerializeEntry(ref writer, key, val, options);
        }
    }

    public static Dictionary<string, object?>? Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;

        var count = reader.ReadMapHeader();
        if (count == 0) return null;

        var dictionary = new Dictionary<string, object?>(count);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString() ?? throw new MessagePackSerializationException("Dictionary key cannot be null.");

            if (reader.TryReadNil())
            {
                dictionary[key] = null;
                continue;
            }

            if (reader.ReadArrayHeader() != 2)
            {
                throw new MessagePackSerializationException("A typed metadata entry needs a code and a value.");
            }

            var typeCode = reader.ReadInt32();
            if (!CodeTypes.TryGetValue(typeCode, out var targetType))
            {
                reader.Skip();
                continue;
            }

            dictionary[key] = MessagePackSerializer.Deserialize(targetType, ref reader, options);
        }

        return dictionary.Count == 0 ? null : dictionary;
    }

    private static void SerializeEntry(
        ref MessagePackWriter writer,
        string key,
        object? value,
        MessagePackSerializerOptions options)
    {
        writer.Write(key);
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        var type = value.GetType();
        if (!TypeCodes.TryGetValue(type, out var typeCode))
        {
            var json = JsonSerializer.SerializeToElement(value, type);
            writer.WriteArrayHeader(2);
            writer.Write(8);
            MessagePackSerializer.Serialize(ref writer, json, options);
            return;
        }

        writer.WriteArrayHeader(2);
        writer.Write(typeCode);
        MessagePackSerializer.Serialize(type, ref writer, value, options);
    }

    void IMessagePackFormatter<MetadataDictionary>.Serialize(
        ref MessagePackWriter writer,
        MetadataDictionary value,
        MessagePackSerializerOptions options)
    {
        Serialize(ref writer, value, options);
    }

    MetadataDictionary IMessagePackFormatter<MetadataDictionary>.Deserialize(
        ref MessagePackReader reader,
        MessagePackSerializerOptions options)
    {
        var dictionary = Deserialize(ref reader, options);
        return dictionary is null ? MetadataDictionary.Empty : MetadataDictionary.FromDictionary(dictionary);
    }
}
