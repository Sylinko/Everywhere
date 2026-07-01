using MessagePack;
using Microsoft.SemanticKernel;

namespace Everywhere.Serialization;

public class FunctionCallContentMessagePackFormatter : FunctionContentMessagePackFormatter<FunctionCallContent>
{
    protected override void SerializeCore(ref MessagePackWriter writer, FunctionCallContent value, MessagePackSerializerOptions options)
    {
        // Use array for backward compatibility
        writer.WriteArrayHeader(5);

        writer.Write(value.Id);
        writer.Write(value.PluginName);
        writer.Write(value.FunctionName);
        MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Arguments, options);
        MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Metadata, options);
    }

    protected override FunctionCallContent DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        string? id = null, pluginName = null, functionName = null;
        KernelArguments? arguments = null;
        Dictionary<string, object?>? metadata = null;

        var count = reader.ReadArrayHeader();
        for (var i = 0; i < count; i++)
        {
            switch (i)
            {
                case 0:
                {
                    id = reader.ReadString();
                    break;
                }
                case 1:
                {
                    pluginName = reader.ReadString();
                    break;
                }
                case 2:
                {
                    functionName = reader.ReadString();
                    break;
                }
                case 3:
                {
                    if (MetadataDictionaryMessagePackFormatter.Deserialize(ref reader, options) is { Count: > 0 } dictionary)
                        arguments = new KernelArguments(dictionary);
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

        if (functionName is null)
        {
            throw new MessagePackSerializationException("FunctionCallContent functionName cannot be null.");
        }

        return new FunctionCallContent(functionName, pluginName, id, arguments)
        {
            Metadata = metadata
        };
    }

    protected override FunctionCallContent LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var id = reader.ReadString();
        var pluginName = reader.ReadString();
        var functionName = reader.ReadString() ?? throw new MessagePackSerializationException("FunctionCallContent functionName cannot be null.");
        var argCount = reader.ReadMapHeader();
        var arguments = new KernelArguments();
        for (var j = 0; j < argCount; j++)
        {
            var key = reader.ReadString() ?? throw new MessagePackSerializationException("FunctionCallContent argument key cannot be null.");
            arguments[key] = reader.ReadString();
        }

        return new FunctionCallContent(functionName, pluginName, id, arguments);
    }
}