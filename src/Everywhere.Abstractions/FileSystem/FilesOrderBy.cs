namespace Everywhere.FileSystem;

/// <summary>
/// Represents the criteria by which files can be ordered.
/// </summary>
public enum FilesOrderBy
{
    Default,
    Name,
    Size,
    Created,
    LastModified
}