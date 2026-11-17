using System.Text.Json;
using Everywhere.Chat;
using Everywhere.Harness;
using Everywhere.Prompting.Documents;
using MessagePack;

namespace Everywhere.Serialization;

/// <summary>
/// MessagePack formatter for <see cref="ToolResultRecord"/>, preserving chat v2 and legacy layouts.
/// </summary>
public class ToolResultRecordMessagePackFormatter : FunctionContentMessagePackFormatter<ToolResultRecord>
{
    protected override void SerializeCore(ref MessagePackWriter writer, ToolResultRecord value, MessagePackSerializerOptions options)
    {
        var hasManifest = value.Files.Length > 0;
        writer.WriteArrayHeader(hasManifest ? 6 : 5);
        writer.Write(value.CallId);
        writer.Write(value.PluginName);
        writer.Write(value.FunctionName);

        writer.WriteArrayHeader(2);
        switch (value.Result)
        {
            case null or string:
            {
                writer.Write((int)ToolResultKind.Text);
                writer.Write((string?)value.Result);
                break;
            }
            case ChatAttachment attachment:
            {
                WriteValue(ref writer, ToolResultKind.Attachment, attachment, options);
                break;
            }
            case PromptNode node:
            {
                WriteValue(ref writer, ToolResultKind.PromptNode, node, options);
                break;
            }
            case ChatFunctionResult structured:
            {
                WriteValue(ref writer, ToolResultKind.StructuredResult, structured, options);
                break;
            }
            case JsonElement json:
            {
                WriteValue(ref writer, ToolResultKind.Json, json, options);
                break;
            }
            case ToolContentResult content:
            {
                WriteValue(ref writer, ToolResultKind.ContentBlocks, content, options);
                break;
            }
            default:
            {
                throw new MessagePackSerializationException($"Unregistered tool result: {value.Result.GetType().Name}.");
            }
        }

        MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Metadata, options);
        if (hasManifest)
        {
            options.Resolver.GetFormatterWithVerify<ToolResultFile[]>().Serialize(ref writer, value.Files, options);
        }
    }

    private static void WriteValue<T>(ref MessagePackWriter writer, ToolResultKind type, T value, MessagePackSerializerOptions options)
    {
        writer.Write((int)type);
        options.Resolver.GetFormatterWithVerify<T>().Serialize(ref writer, value, options);
    }

    protected override ToolResultRecord DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var count = reader.ReadArrayHeader();
        var callId = count > 0 ? reader.ReadString() : null;
        var plugin = count > 1 ? reader.ReadString() : null;
        var name = count > 2 ? reader.ReadString() : null;
        var result = count > 3 ? ReadValue(ref reader, options) : null;
        var metadata = count > 4 ? MetadataDictionaryMessagePackFormatter.Deserialize(ref reader, options) : null;
        var files = count > 5 ? options.Resolver.GetFormatterWithVerify<ToolResultFile[]>().Deserialize(ref reader, options) : [];
        for (var index = 6; index < count; index++)
        {
            reader.Skip();
        }

        return new ToolResultRecord(name, plugin, callId, result)
        {
            Metadata = metadata,
            Files = files
        };
    }

    protected override ToolResultRecord LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var callId = reader.ReadString();
        var plugin = reader.ReadString();
        var name = reader.ReadString();
        return new ToolResultRecord(name, plugin, callId, ReadValue(ref reader, options));
    }

    private static object? ReadValue(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        if (reader.ReadArrayHeader() != 2)
        {
            throw new MessagePackSerializationException("A tool result needs a type and a value.");
        }

        var type = (ToolResultKind)reader.ReadInt32();
        switch (type)
        {
            case ToolResultKind.Text:
            {
                return reader.ReadString();
            }
            case ToolResultKind.Attachment:
            {
                return options.Resolver.GetFormatterWithVerify<ChatAttachment>().Deserialize(ref reader, options);
            }
            case ToolResultKind.PromptNode:
            {
                return options.Resolver.GetFormatterWithVerify<PromptNode>().Deserialize(ref reader, options);
            }
            case ToolResultKind.StructuredResult:
            {
                return options.Resolver.GetFormatterWithVerify<ChatFunctionResult>().Deserialize(ref reader, options);
            }
            case ToolResultKind.Json:
            {
                return options.Resolver.GetFormatterWithVerify<JsonElement>().Deserialize(ref reader, options);
            }
            case ToolResultKind.ContentBlocks:
            {
                return options.Resolver.GetFormatterWithVerify<ToolContentResult>().Deserialize(ref reader, options);
            }
            default:
            {
                reader.Skip();
                return null;
            }
        }
    }
}
