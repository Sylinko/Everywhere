using Everywhere.Chat;
using MessagePack;
using Microsoft.SemanticKernel;

namespace Everywhere.Serialization;

public class FunctionResultContentMessagePackFormatter : FunctionContentMessagePackFormatter<FunctionResultContent>
{
    protected override void SerializeCore(ref MessagePackWriter writer, FunctionResultContent value, MessagePackSerializerOptions options)
    {
        // Use array for backward compatibility
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
                {
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
                    }

                    break;
                }
                case 4:
                {
                    metadata = MetadataDictionaryMessagePackFormatter.Deserialize(ref reader, options);
                    break;
                }
                default:
                {
                    // Skip unknown fields for forward compatibility
                    reader.Skip();
                    break;
                }
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
        object? value = null;
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
        }

        return new FunctionResultContent(functionName, pluginName, callId, value);
    }
}