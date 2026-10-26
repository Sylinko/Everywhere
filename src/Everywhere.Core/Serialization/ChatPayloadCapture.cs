using Everywhere.Chat;
using MessagePack;
using MessagePack.Formatters;

namespace Everywhere.Serialization;

/// <summary>
/// Captures one durable message and its file dependencies in the same serialization pass.
/// Parent and nested graphs are serialized together in their shared execution domain. No presentation or
/// deserialized copy of the message graph is needed to derive the dependency projection.
/// </summary>
public sealed record ChatPayloadCapture(byte[] Payload, Guid? SummaryThroughNodeId, IReadOnlyList<FileAttachment> Files)
{
    /// <summary>
    /// Serializes a fully loaded message, including any embedded child conversations.
    /// Mutable attachment paths are copied before both the payload and dependency list use them.
    /// </summary>
    public static ChatPayloadCapture Capture(ChatMessage message, CancellationToken cancellationToken = default)
    {
        var options = MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.None);
        var resolver = new CaptureResolver(options.Resolver);
        var payload = MessagePackSerializer.Serialize(message, options.WithResolver(resolver), cancellationToken);
        var summaryThrough = message is ContextCompressionChatMessage { HasSummary: true } compression ?
            compression.CoveredThroughNodeId : (Guid?)null;
        return new ChatPayloadCapture(payload, summaryThrough, resolver.Files);
    }

    [ExcludeFormatterFromSourceGeneratedResolver]
    private sealed class CaptureResolver(IFormatterResolver fallback) : IFormatterResolver,
        IMessagePackFormatter<FileAttachment?>
    {
        public List<FileAttachment> Files { get; } = [];

        public IMessagePackFormatter<T>? GetFormatter<T>()
        {
            return this is IMessagePackFormatter<T> formatter ? formatter : fallback.GetFormatter<T>();
        }

        public void Serialize(ref MessagePackWriter writer, FileAttachment? value, MessagePackSerializerOptions options)
        {
            if (value is null)
            {
                writer.WriteNil();
                return;
            }

            var file = new FileAttachment(value.HeaderKey, value.FilePath, value.Sha256, value.MimeType, value.Description);
            fallback.GetFormatterWithVerify<FileAttachment>().Serialize(ref writer, file, options);
            Files.Add(file);
        }

        FileAttachment? IMessagePackFormatter<FileAttachment?>.Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            return fallback.GetFormatterWithVerify<FileAttachment>().Deserialize(ref reader, options);
        }
    }
}