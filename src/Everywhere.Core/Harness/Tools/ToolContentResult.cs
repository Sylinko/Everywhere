using System.Text.Json;
using MessagePack;

namespace Everywhere.Harness;

/// <summary>
/// Represents ordered multimodal or structured tool output.
/// Protocol metadata stays private; structured content and error status accompany the public blocks.
/// </summary>
[MessagePackObject]
public sealed partial class ToolContentResult
{
    [Key(0)]
    public required ToolContentBlock[] Blocks { get; init; }

    [Key(1)]
    public JsonElement? StructuredContent { get; init; }

    [Key(2)]
    public bool? IsError { get; init; }

    [Key(3)]
    public JsonElement? ProtocolMetadata { get; init; }

    /// <summary>
    /// Renders public content for export without exposing protocol details or local paths.
    /// </summary>
    public string GetPublicText(IReadOnlyList<ToolResultFile> files)
    {
        var lines = new List<string>();
        foreach (var block in Blocks)
        {
            switch (block)
            {
                case ToolContentBlock.Text text:
                {
                    lines.Add(text.Value);
                    break;
                }
                case ToolContentBlock.Uri uri:
                {
                    lines.Add(uri.Address);
                    break;
                }
                case ToolContentBlock.File file when (uint)file.Index < (uint)files.Count:
                {
                    lines.Add($"[Attachment: {files[file.Index].MimeType}]");
                    break;
                }
                case ToolContentBlock.Json json:
                {
                    lines.Add(json.Value.GetRawText());
                    break;
                }
            }
        }

        if (StructuredContent is { } structured)
        {
            lines.Add(structured.GetRawText());
        }

        if (IsError is true)
        {
            lines.Add("The tool reported an error.");
        }

        return string.Join("\n", lines);
    }
}

/// <summary>
/// Permanent content block identifiers within a rich tool result.
/// </summary>
public enum ToolContentBlockKind
{
    Text = 0,
    File = 1,
    Uri = 2,
    Json = 3
}

/// <summary>
/// Represents a numbered block within a <see cref="ToolContentResult"/>.
/// Binary content indexes the enclosing result's file manifest.
/// </summary>
[MessagePackFormatter(typeof(Serialization.ToolContentBlockFormatter))]
public abstract record ToolContentBlock
{
    public sealed record Text(string Value) : ToolContentBlock;
    public sealed record File(int Index) : ToolContentBlock;
    public sealed record Uri(string Address, string? MediaType) : ToolContentBlock;
    public sealed record Json(JsonElement Value) : ToolContentBlock;

    /// <summary>
    /// Gets optional private protocol details associated with this block.
    /// </summary>
    public JsonElement? ProtocolDetails { get; init; }

    /// <summary>
    /// Gets optional typed MEAI content properties associated with this block.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }
}
