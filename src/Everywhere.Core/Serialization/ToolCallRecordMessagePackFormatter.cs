using Everywhere.Harness;
using MessagePack;

namespace Everywhere.Serialization;

/// <summary>
/// MessagePack formatter for <see cref="ToolCallRecord"/>, preserving chat v2 and legacy layouts.
/// </summary>
public class ToolCallRecordMessagePackFormatter : FunctionContentMessagePackFormatter<ToolCallRecord>
{
    protected override void SerializeCore(ref MessagePackWriter writer, ToolCallRecord value, MessagePackSerializerOptions options)
    {
        writer.WriteArrayHeader(5);
        writer.Write(value.Id);
        writer.Write(value.PluginName);
        writer.Write(value.FunctionName);
        MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Arguments, options);
        MetadataDictionaryMessagePackFormatter.Serialize(ref writer, value.Metadata, options);
    }

    protected override ToolCallRecord DeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        string? id = null, pluginName = null, functionName = null;
        Dictionary<string, object?>? arguments = null;
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
                    {
                        arguments = dictionary;
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
                    reader.Skip();
                    break;
                }
            }
        }

        if (functionName is null)
        {
            throw new MessagePackSerializationException("ToolCallRecord functionName cannot be null.");
        }

        return new ToolCallRecord(functionName, pluginName, id, arguments)
        {
            Metadata = metadata
        };
    }

    protected override ToolCallRecord LegacyDeserializeCore(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var id = reader.ReadString();
        var pluginName = reader.ReadString();
        var functionName = reader.ReadString() ?? throw new MessagePackSerializationException("ToolCallRecord functionName cannot be null.");
        var argCount = reader.ReadMapHeader();
        var arguments = new Dictionary<string, object?>();
        for (var j = 0; j < argCount; j++)
        {
            var key = reader.ReadString() ?? throw new MessagePackSerializationException("ToolCallRecord argument key cannot be null.");
            arguments[key] = reader.ReadString();
        }

        return new ToolCallRecord(functionName, pluginName, id, arguments);
    }
}
