using System.Text.Json;
using Everywhere.Harness;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

/// <summary>
/// MessagePack formatter for <see cref="ToolContentBlock"/>.
/// </summary>
public sealed class ToolContentBlockFormatter : IMessagePackFormatter<ToolContentBlock?>
{
    public void Serialize(ref MessagePackWriter writer, ToolContentBlock? value, MessagePackSerializerOptions options)
    {
        if (value is null) throw new MessagePackSerializationException("A result cannot contain a null block.");

        var hasProperties = value.Properties is { Count: > 0 };
        var hasDetails = value.ProtocolDetails.HasValue || hasProperties;
        var knownFields = hasProperties ? 4 : hasDetails ? 3 : 2;
        writer.WriteArrayHeader(knownFields);
        switch (value)
        {
            case ToolContentBlock.Text text:
            {
                writer.Write((int)ToolContentBlockKind.Text);
                writer.Write(text.Value);
                break;
            }
            case ToolContentBlock.File file:
            {
                writer.Write((int)ToolContentBlockKind.File);
                writer.Write(file.Index);
                break;
            }
            case ToolContentBlock.Uri uri:
            {
                writer.Write((int)ToolContentBlockKind.Uri);
                writer.WriteArrayHeader(2);
                writer.Write(uri.Address);
                writer.Write(uri.MediaType);
                break;
            }
            case ToolContentBlock.Json json:
            {
                writer.Write((int)ToolContentBlockKind.Json);
                options.Resolver.GetFormatterWithVerify<JsonElement>().Serialize(ref writer, json.Value, options);
                break;
            }
            default:
            {
                throw new MessagePackSerializationException($"Unregistered tool block: {value.GetType().Name}.");
            }
        }

        if (hasDetails)
        {
            options.Resolver.GetFormatterWithVerify<JsonElement?>().Serialize(ref writer, value.ProtocolDetails, options);
        }

        if (hasProperties)
        {
            MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Properties, options);
        }
    }

    public ToolContentBlock Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var count = reader.ReadArrayHeader();
        if (count < 2) throw new MessagePackSerializationException("A tool block needs a type and a value.");

        var type = (ToolContentBlockKind)reader.ReadInt32();
        ToolContentBlock block = type switch
        {
            ToolContentBlockKind.Text => new ToolContentBlock.Text(
                reader.ReadString() ?? throw new MessagePackSerializationException("Missing block text.")),
            ToolContentBlockKind.File => new ToolContentBlock.File(reader.ReadInt32()),
            ToolContentBlockKind.Uri => ReadUri(ref reader),
            ToolContentBlockKind.Json => new ToolContentBlock.Json(
                options.Resolver.GetFormatterWithVerify<JsonElement>().Deserialize(ref reader, options)),
            _ => SkipUnknownBlock(ref reader)
        };
        if (count == 2) return block;

        var details = options.Resolver.GetFormatterWithVerify<JsonElement?>().Deserialize(ref reader, options);
        var properties = count > 3 ? MetadataDictionaryMessagePackFormatter.Deserialize(ref reader, options) : null;
        for (var i = 4; i < count; i++)
        {
            reader.Skip();
        }

        return block with { ProtocolDetails = details, Properties = properties };
    }

    private static ToolContentBlock.Uri ReadUri(ref MessagePackReader reader)
    {
        if (reader.ReadArrayHeader() != 2) throw new MessagePackSerializationException("Invalid URI block.");
        var address = reader.ReadString() ?? throw new MessagePackSerializationException("Missing block URI.");
        return new ToolContentBlock.Uri(address, reader.ReadString());
    }

    private static ToolContentBlock.Text SkipUnknownBlock(ref MessagePackReader reader)
    {
        reader.Skip();
        return new ToolContentBlock.Text(string.Empty);
    }
}
