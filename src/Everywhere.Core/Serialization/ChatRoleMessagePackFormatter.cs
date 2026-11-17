using MessagePack;
using MessagePack.Formatters;
using Microsoft.Extensions.AI;

namespace Everywhere.Serialization;

public class ChatRoleMessagePackFormatter : IMessagePackFormatter<ChatRole>
{
    public ChatRole Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        return new ChatRole(reader.ReadString() ?? throw new MessagePackSerializationException("ChatRole label cannot be null."));
    }

    public void Serialize(ref MessagePackWriter writer, ChatRole value, MessagePackSerializerOptions options)
    {
        writer.Write(value.Value);
    }
}