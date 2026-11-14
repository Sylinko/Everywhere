using Everywhere.Utilities;
using Lucide.Avalonia;

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
    public static LucideIconKind FromPath(string path, bool isDirectory = false)
    {
        if (isDirectory || Path.EndsInDirectorySeparator(path)) return LucideIconKind.Folder;

        var extension = Path.GetExtension(path);

        // EXE files and DLLs share a MIME type; only EXE files get the application icon.
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return LucideIconKind.AppWindow;
        }

        if (!FileUtilities.KnownMimeTypes.TryGetValue(extension, out var mimeType))
        {
            return LucideIconKind.File;
        }

        return FileUtilities.GetCategory(mimeType) switch
        {
            FileTypeCategory.Image => LucideIconKind.FileImage,
            FileTypeCategory.Audio => LucideIconKind.FileMusic,
            FileTypeCategory.Video => LucideIconKind.FileVideoCamera,
            FileTypeCategory.Document => LucideIconKind.FileText,
            FileTypeCategory.Archive => LucideIconKind.FileArchive,
            FileTypeCategory.Script => LucideIconKind.FileCode,
            _ => LucideIconKind.File
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