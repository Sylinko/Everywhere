using Everywhere.Prompting.Documents;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

/// <summary>
/// Serializes structured visual-context prompt content while accepting the legacy flattened string representation.
/// </summary>
public sealed class VisualContextPromptMessagePackFormatter : IMessagePackFormatter<PromptNode?>
{
    /// <inheritdoc />
    public void Serialize(ref MessagePackWriter writer, PromptNode? value, MessagePackSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNil();
            return;
        }

        options.Resolver.GetFormatterWithVerify<PromptNode>().Serialize(ref writer, value, options);
    }

    /// <inheritdoc />
    public PromptNode? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.TryReadNil()) return null;
        if (reader.NextMessagePackType == MessagePackType.String)
        {
            return reader.ReadString() is { } text ? new PromptText(text) : null;
        }

        if (reader.NextMessagePackType == MessagePackType.Array)
        {
            return options.Resolver.GetFormatterWithVerify<PromptNode>().Deserialize(ref reader, options);
        }

        throw new MessagePackSerializationException($"Unsupported visual-context prompt value type '{reader.NextMessagePackType}'.");
    }
}