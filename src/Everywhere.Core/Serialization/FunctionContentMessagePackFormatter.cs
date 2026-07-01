using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

public abstract class FunctionContentMessagePackFormatter<T> : IMessagePackFormatter<T?> where T : class
{
    public void Serialize(ref MessagePackWriter writer, T? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        SerializeCore(ref writer, value, options);
    }

    public T? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil())
        {
            return null;
        }

        if (reader.NextMessagePackType != MessagePackType.Array)
        {
            return LegacyDeserializeCore(ref reader, options);
        }

        return DeserializeCore(ref reader, options);
    }

    protected abstract void SerializeCore(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options);

    protected abstract T DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options);

    protected abstract T LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options);
}