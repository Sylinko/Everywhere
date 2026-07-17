using System.Text;

namespace Everywhere.Skills;

/// <summary>
/// Defines a virtual skill whose files are supplied from memory instead of a physical directory.
/// </summary>
public sealed record VirtualSkill(
    string Id,
    string MarkdownContent,
    IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Resources
)
{
    public VirtualSkill(string id, string markdownContent, IReadOnlyDictionary<string, string>? resources = null)
        : this(
            id,
            markdownContent,
            resources?.ToDictionary(
                static item => item.Key,
                static item => (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(item.Value),
                StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, ReadOnlyMemory<byte>>(StringComparer.OrdinalIgnoreCase))
    {
    }
}