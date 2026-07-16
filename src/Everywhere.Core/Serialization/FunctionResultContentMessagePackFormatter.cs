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
                var formatter = options.Resolver.GetFormatterWithVerify<ChatAttachment>();
                writer.Write(1);
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

        if (reader.ReadArrayHeader() != 5)
        {
            throw new MessagePackSerializationException("FunctionResultContent array header must be 5.");
        }

        callId = reader.ReadString();
        pluginName = reader.ReadString();
        functionName = reader.ReadString();

        if (reader.ReadArrayHeader() != 2)
        {
            throw new MessagePackSerializationException("FunctionResultContent result array header must be 2.");
        }

        var valueType = reader.ReadInt32();
        switch (valueType)
        {
            case 0:
            {
                result = reader.ReadString();
                break;
            }
            case 1:
            {
                var formatter = options.Resolver.GetFormatterWithVerify<ChatAttachment>();
                result = formatter.Deserialize(ref reader, options);
                break;
            }
            case 2:
            {
                var formatter = options.Resolver.GetFormatterWithVerify<PromptNode>();
                result = formatter.Deserialize(ref reader, options);
                break;
            }
            default:
            {
                throw new MessagePackSerializationException($"Unknown FunctionResultContent result type '{valueType}'.");
            }
        }

        metadata = MetadataDictionaryMessagePackFormatter.Deserialize(ref reader, options);

        return new FunctionResultContent(functionName, pluginName, callId, result)
        {
            Metadata = metadata
        };
    }

    protected override FunctionResultContent LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        throw new MessagePackSerializationException("FunctionResultContent must use the canonical array representation.");
    }
}
