using System.Text;

namespace Everywhere.FileSystem;

/// <summary>
/// Represents the content of a file read by a chat plugin.
/// </summary>
/// <remarks>
/// This record is not used for serialization; use ToString() for text representation.
/// </remarks>
/// <param name="Content"></param>
/// <param name="IsBinary"></param>
/// <param name="BytesRead"></param>
/// <param name="BytesLeft"></param>
public readonly record struct FileContent(string Content, bool IsBinary, long BytesRead, long BytesLeft)
{
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(IsBinary ? "binary" : "text")
            .Append(" file (")
            .Append(BytesRead)
            .Append(" bytes read, ")
            .Append(BytesLeft)
            .AppendLine(" bytes left)");
        sb.AppendLine("-----BEGIN FILE CONTENT-----");
        sb.Append(Content);
        sb.AppendLine("-----END FILE CONTENT-----");
        return sb.ToString();
    }
}