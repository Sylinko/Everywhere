namespace Everywhere.Skills;

/// <summary>
/// Represents a resolved physical or in-memory resource belonging to a skill.
/// </summary>
public sealed record SkillResource(
    string Uri,
    string RelativePath,
    bool IsDirectory,
    string? PhysicalPath,
    ReadOnlyMemory<byte> Content
)
{
    public long Length => PhysicalPath != null && File.Exists(PhysicalPath) ? new FileInfo(PhysicalPath).Length : Content.Length;

    public Stream OpenRead() =>
        PhysicalPath != null ?
            File.Open(PhysicalPath, FileMode.Open, FileAccess.Read, FileShare.Read) :
            new MemoryStream(Content.ToArray(), writable: false);
}