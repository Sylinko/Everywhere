namespace Everywhere.Chat.Links;

/// <summary>
/// Classifies link text without touching the filesystem or resolving relative paths.
/// </summary>
public sealed record ChatLinkTarget(ChatLinkKind Kind, string Address)
{
    /// <summary>
    /// Encodes a native path, including literal percent characters, as a file URI for Markdown or D-Bus.
    /// </summary>
    public static string ToFileUri(string absolutePath)
    {
        var path = OperatingSystem.IsWindows() ? absolutePath.Replace('\\', '/') : absolutePath;
        var escaped = Uri.EscapeDataString(path).Replace("%2F", "/", StringComparison.Ordinal);
        if (OperatingSystem.IsWindows())
        {
            if (path.StartsWith("//", StringComparison.Ordinal))
            {
                return "file:" + escaped;
            }

            // Only the drive separator is URI syntax. Percent signs in native filenames are data.
            return "file:///" + path[..2] + escaped[4..];
        }

        return "file://" + escaped;
    }

    public static ChatLinkTarget Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
        }

        // Check native paths before URI parsing: a literal '%' in a filename is not URI encoding.
        var isWindowsPath = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/';
        var isUncPath = value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal);
        if (isWindowsPath || isUncPath)
        {
            return OperatingSystem.IsWindows() ? Local(value) : new ChatLinkTarget(ChatLinkKind.Unsupported, value);
        }

        if (value.StartsWith('/'))
        {
            return OperatingSystem.IsWindows() ? new ChatLinkTarget(ChatLinkKind.Unsupported, value) : Local(value);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
        }

        if (uri.Scheme is "http" or "https")
        {
            return new ChatLinkTarget(ChatLinkKind.Web, uri.AbsoluteUri);
        }

        if (uri.IsFile)
        {
            if (!value.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
            }

            if (!OperatingSystem.IsWindows() && !string.IsNullOrEmpty(uri.Host) && uri.Host != "localhost")
            {
                return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
            }

            var path = uri.LocalPath;
            if (!Path.IsPathFullyQualified(path))
            {
                return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
            }

            // Uri may map file:///C:/... to /C:/... on Unix. Do not mistake it for a native path.
            if (!OperatingSystem.IsWindows() && path is ['/', _, _, _, ..] && char.IsAsciiLetter(path[1]) && path[2] == ':')
            {
                return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
            }

            return Local(path);
        }

        return new ChatLinkTarget(ChatLinkKind.Unsupported, value);
    }

    private static ChatLinkTarget Local(string path)
    {
        try
        {
            if (path.Any(char.IsControl))
            {
                return new ChatLinkTarget(ChatLinkKind.Unsupported, path);
            }

            var absolutePath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(absolutePath);
            if (OperatingSystem.IsWindows() &&
                (absolutePath.StartsWith(@"\\?\", StringComparison.Ordinal) ||
                    absolutePath.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                    root is null ||
                    absolutePath.AsSpan(root.Length).Contains(':')))
            {
                return new ChatLinkTarget(ChatLinkKind.Unsupported, path);
            }

            return new ChatLinkTarget(ChatLinkKind.Local, absolutePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ChatLinkTarget(ChatLinkKind.Unsupported, path);
        }
    }
}

public enum ChatLinkKind
{
    Web,
    Local,
    Unsupported
}

public enum ChatLinkAction
{
    Reveal,
    Open,
    OpenWith,
    SaveAs,
    CopyAddress,
    CopyText,
    FollowLocalLink
}

public enum ChatLinkOperationStatus
{
    Accepted,
    ParentOpened,
    Canceled,
    Unavailable,
    Failed,
    NoAssociatedApplication,
    MissingTarget,
    RequiresFile,
    SameFile
}