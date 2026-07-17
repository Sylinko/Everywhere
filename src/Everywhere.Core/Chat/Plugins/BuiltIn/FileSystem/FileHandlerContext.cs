namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Represents an immutable, fully resolved association between a resource and its handler.
/// </summary>
public sealed class FileHandlerContext(FileHandler handler, string path, string workingDirectory, FileSystemInfo? fileSystemInfo = null)
{
    public string Path { get; } = path;

    public FileHandler Handler { get; } = handler;

    internal string WorkingDirectory { get; } = workingDirectory;

    public FileSystemInfo? FileSystemInfo { get; } = fileSystemInfo;
}