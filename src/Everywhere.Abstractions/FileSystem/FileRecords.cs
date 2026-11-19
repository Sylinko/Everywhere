using System.Text;

namespace Everywhere.FileSystem;

/// <summary>
/// Represents a collection of file records returned by a chat plugin.
/// </summary>
/// <remarks>
/// This record is not used for serialization; use ToString() for text representation.
/// </remarks>
/// <param name="Results"></param>
/// <param name="TotalCount"></param>
public readonly record struct FileRecords(IEnumerable<FileRecord> Results, long? TotalCount = null)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append("Count: ").AppendLine(TotalCount?.ToString() ?? Results.Count().ToString());
        sb.AppendLine(FileRecord.Header).AppendLine("----");
        foreach (var record in Results) sb.AppendLine(record.ToString());
        return sb.ToString();
    }
}