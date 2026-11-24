using Everywhere.Utilities;
using Tabler.Avalonia;

namespace Everywhere.Chat.Links;

/// <summary>
/// Chooses icons for chat file and directory references.
/// </summary>
public static class ChatFileIcons
{
    private static readonly SemaphoreSlim DirectoryProbeSlots = new(4);

    /// <summary>
    /// Uses known directory information or a trailing path separator, then falls back to MIME classification.
    /// Does not access the filesystem.
    /// </summary>
    public static TablerIconKind FromPath(string path, bool isDirectory = false)
    {
        if (isDirectory || Path.EndsInDirectorySeparator(path)) return TablerIconKind.Folder;

        var extension = Path.GetExtension(path);

        // EXE files and DLLs share a MIME type; only EXE files get the application icon.
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return TablerIconKind.AppWindow;
        }

        if (!FileUtilities.KnownMimeTypes.TryGetValue(extension, out var mimeType))
        {
            return TablerIconKind.File;
        }

        return FileUtilities.GetCategory(mimeType) switch
        {
            FileTypeCategory.Image => TablerIconKind.Photo,
            FileTypeCategory.Audio => TablerIconKind.FileMusic,
            FileTypeCategory.Video => TablerIconKind.Video,
            FileTypeCategory.Document => TablerIconKind.FileText,
            FileTypeCategory.Archive => TablerIconKind.FileZip,
            FileTypeCategory.Script => TablerIconKind.FileCode,
            _ => TablerIconKind.File
        };
    }

    internal static async Task<bool> ProbeDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        // Never probe explicit network shares for an icon. Slow mounted drives can still block a worker.
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return false;

        await DirectoryProbeSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Directory.Exists(path), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            DirectoryProbeSlots.Release();
        }
    }
}