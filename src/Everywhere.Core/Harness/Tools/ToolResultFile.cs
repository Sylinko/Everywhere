using Everywhere.Chat;
using MessagePack;

namespace Everywhere.Harness;

/// <summary>
/// Represents a portable content-addressed resource descriptor for a tool result file.
/// Only a locally resolved snapshot supplies model input.
/// </summary>
[MessagePackObject]
public sealed partial class ToolResultFile(string sha256, string mimeType)
{
    [Key(0)]
    public string Sha256 { get; } = sha256;

    [Key(1)]
    public string MimeType { get; } = mimeType;

    [IgnoreMember]
    public FileAttachment Attachment { get; } = new(new DirectLocaleKey(mimeType), string.Empty, sha256, mimeType);
}
