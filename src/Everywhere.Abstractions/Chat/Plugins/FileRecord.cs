using Everywhere.Utilities;

namespace Everywhere.Chat.Plugins;

/// <summary>
/// Represents a file or directory record with metadata.
/// </summary>
/// <remarks>
/// This record is not used for serialization; use ToString() for text representation.
/// </remarks>
/// <param name="FullPath"></param>
/// <param name="BytesSize">-1 indicates a directory</param>
/// <param name="Created"></param>
/// <param name="Modified"></param>
/// <param name="Attributes"></param>
public readonly record struct FileRecord(
    string FullPath,
    long BytesSize,
    DateTime? Created,
    DateTime? Modified,
    FileAttributes Attributes
)
{
    public string HumanizedSize => BytesSize switch
    {
        >= 1024 => $"{BytesSize} bytes ({Humanizer.HumanizeBytes(BytesSize)})",
        > 0 => $"{BytesSize} bytes",
        0 => "0",
        _ => "<DIR>"
    };

    public const string Header = "Path\tSize\tCreated\tModified\tAttributes";

    private static string HumanizeDate(DateTime? date) => date?.ToString("G") ?? "N/A";

    public override string ToString() => $"{FullPath}\t{HumanizedSize}\t{HumanizeDate(Created)}\t{HumanizeDate(Modified)}\t{Attributes}";
}