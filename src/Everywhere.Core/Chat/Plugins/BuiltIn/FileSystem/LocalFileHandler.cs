using System.Text.RegularExpressions;
using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Provides path resolution, metadata, enumeration, and stream helpers for local handlers.
/// </summary>
public abstract class LocalFileHandler : FileHandler
{
    public sealed override async ValueTask<FileHandlerContext?> TryCreateContextAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(path, UriKind.Absolute, out var uri))
        {
            if (!uri.IsFile) return null;
            path = uri.LocalPath;
        }

        try
        {
            // Resolve before format probing so every local handler sees the same canonical identity.
            path = FileSystemPlugin.ExpandFullPath(workingDirectory, path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new HandledException(
                new ArgumentException($"The local path '{path}' could not be resolved: {ex.Message}", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        FileSystemInfo info = File.Exists(path) ? new FileInfo(path)
            : Directory.Exists(path) ? new DirectoryInfo(path)
            : new FileInfo(path);

        return await CanHandleLocalAsync(path, info, cancellationToken) ? new FileHandlerContext(this, path, workingDirectory, info) : null;
    }

    public override ValueTask<FileRecord> GetFileInformationAsync(FileHandlerContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = context.FileSystemInfo ??
            throw new HandledException(
                new InvalidOperationException(
                    $"The local file context for '{context.Path}' has no file-system metadata, so the operation cannot continue."),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        if (!info.Exists)
        {
            throw new HandledException(
                new FileNotFoundException($"The requested local path does not exist: '{context.Path}'.", context.Path),
                LocaleKey.HandledSystemException_FileNotFound);
        }

        return ValueTask.FromResult(
            new FileRecord(
                info.FullName,
                info is FileInfo file ? file.Length : -1,
                info.CreationTime,
                info.LastWriteTime,
                info.Attributes));
    }

    // ReSharper disable once AsyncMethodWithoutAwait
    public override async IAsyncEnumerable<string> EnumerateAsync(
        FileHandlerContext context,
        Regex filePattern,
        bool recurseSubdirectories,
        bool ignoreCommonBuildFolders,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (context.FileSystemInfo is FileInfo file)
        {
            if (file.Exists && filePattern.IsMatch(file.Name)) yield return file.FullName;
            yield break;
        }

        if (context.FileSystemInfo is not DirectoryInfo { Exists: true } directory)
        {
            throw new HandledException(
                new DirectoryNotFoundException(
                    $"The requested path is not a directory, so it cannot be enumerated: '{context.Path}'."),
                LocaleKey.HandledSystemException_DirectoryNotFound);
        }

        var directories = new Stack<(string Path, int Depth)>();
        directories.Push((directory.FullName, 0));

        while (directories.TryPop(out var current))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] entries;
            try
            {
                // Directory.EnumerateFileSystemEntries is lazy; materialize this directory while the
                // exception guard is active so failures from MoveNext do not abort the whole traversal.
                entries = Directory.EnumerateFileSystemEntries(current.Path).ToArray();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    continue;
                }

                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var name = Path.GetFileName(entry);
                if (filePattern.IsMatch(name)) yield return Path.GetFullPath(entry);

                if (!isDirectory || !recurseSubdirectories || current.Depth >= 32) continue;
                if (ignoreCommonBuildFolders && ShouldIgnoreDirectory(name, attributes)) continue;
                directories.Push((entry, current.Depth + 1));
            }
        }
    }

    protected abstract ValueTask<bool> CanHandleLocalAsync(string path, FileSystemInfo fileSystemInfo, CancellationToken cancellationToken);

    protected static FileInfo EnsureFile(FileHandlerContext context)
    {
        if (context.FileSystemInfo is FileInfo { Exists: true } file) return file;
        if (context.FileSystemInfo is DirectoryInfo { Exists: true })
        {
            throw new HandledException(
                new InvalidOperationException(
                    $"The requested path is a directory, but this operation requires a file: '{context.Path}'."),
                LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathIsDirectory_ErrorMessage);
        }

        throw new HandledException(
            new FileNotFoundException($"The requested path is not an existing file: '{context.Path}'.", context.Path),
            LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathNotExist_ErrorMessage);
    }

    protected static FileStream Open(
        FileHandlerContext context,
        FileMode mode,
        FileAccess access,
        FileShare share) =>
        new(context.Path, mode, access, share);

    private static bool ShouldIgnoreDirectory(string name, FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0 ||
        name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("obj", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
        name.Equals(".vs", StringComparison.OrdinalIgnoreCase);
}