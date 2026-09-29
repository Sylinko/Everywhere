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

            reader.ReadArrayHeader();
            var typeCode = reader.ReadInt32();
            if (typeCode == 0 || !CodeTypes.TryGetValue(typeCode, out var targetType))
            {
                throw new MessagePackSerializationException($"Unsupported dictionary value type code: {typeCode}");
            }

            var val = MessagePackSerializer.Deserialize(targetType, ref reader, options);
            dictionary[key] = val;
        }

        return dictionary;
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
            throw new MessagePackSerializationException($"Unsupported dictionary value type: {type.FullName}");
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