using Everywhere.Chat;
using Everywhere.Chat.Documents;
using MessagePack;
using Microsoft.SemanticKernel;

namespace Everywhere.Serialization;

/// <summary>
/// Serializes function results, including structured prompt nodes, in one canonical representation.
/// </summary>
public class FunctionResultContentMessagePackFormatter : FunctionContentMessagePackFormatter<FunctionResultContent>
{
    protected override void SerializeCore(ref MessagePackWriter writer, FunctionResultContent value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(5);

        writer.Write(value.CallId);
        writer.Write(value.PluginName);
        writer.Write(value.FunctionName);

        writer.WriteArrayHeader(2);
        switch (value.Result)
        {
            case ChatAttachment chatAttachment:
            {
                writer.Write(1);
                var formatter = options.Resolver.GetFormatterWithVerify<ChatAttachment>();
                formatter.Serialize(ref writer, chatAttachment, options);
                break;
            }
            case PromptNode promptNode:
            {
                writer.Write(2);
                var formatter = options.Resolver.GetFormatterWithVerify<PromptNode>();
                formatter.Serialize(ref writer, promptNode, options);
                break;
            }
            default:
            {
                writer.Write(0);
                writer.Write(value.Result?.ToString());
                break;
            }
        }

        MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Metadata, options);
    }

    protected override FunctionResultContent DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        string? callId = null, pluginName = null, functionName = null;
        object? result = null;
        Dictionary<string, object?>? metadata = null;

        var count = reader.ReadArrayHeader();
        for (var i = 0; i < count; i++)
        {
            switch (i)
            {
                case 0:
                    callId = reader.ReadString();
                    break;
                case 1:
                    pluginName = reader.ReadString();
                    break;
                case 2:
                    functionName = reader.ReadString();
                    break;
                case 3:
                    if (reader.ReadArrayHeader() != 2)
                    {
                        throw new MessagePackSerializationException("FunctionResultContent result array header must be 2.");
                    }

                    var valueType = reader.ReadInt32();
                    result = valueType switch
                    {
                        0 => reader.ReadString(),
                        1 => options.Resolver.GetFormatterWithVerify<ChatAttachment>().Deserialize(ref reader, options),
                        2 => options.Resolver.GetFormatterWithVerify<PromptNode>().Deserialize(ref reader, options),
                        _ => throw new MessagePackSerializationException($"Unknown FunctionResultContent result type '{valueType}'.")
                    };
                    break;
                case 4:
                    metadata = MetadataDictionaryMessagePackFormatter.Deserialize(ref reader, options);
                    break;
                default:
                    // Canonical fields are append-only; older clients may have persisted a longer layout.
                    reader.Skip();
                    break;
            }
        }

        return new FunctionResultContent(functionName, pluginName, callId, result)
        {
            Metadata = metadata
        };
    }

    protected override FunctionResultContent LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var callId = reader.ReadString();
        var pluginName = reader.ReadString();
        var functionName = reader.ReadString();

        if (reader.ReadArrayHeader() != 2)
        {
            throw new MessagePackSerializationException("FunctionResultContent array header must be 2.");
        }

        var valueType = reader.ReadInt32();
        object? value;
        switch (valueType)
        {
            case 0:
            {
                value = reader.ReadString();
                break;
            }
            case 1:
            {
                var formatter = options.Resolver.GetFormatterWithVerify<ChatAttachment>();
                value = formatter.Deserialize(ref reader, options);
                break;
            }
            default:
            {
                throw new MessagePackSerializationException($"Unknown FunctionResultContent value type '{valueType}'.");
            }
        }

        return new FunctionResultContent(functionName, pluginName, callId, value);
    }
}
