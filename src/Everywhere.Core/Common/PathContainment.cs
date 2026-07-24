namespace Everywhere.Common;

/// <summary>
/// Resolves existing path components and checks containment without trusting only lexical prefixes.
/// </summary>
public static class PathContainment
{
    /// <summary>
    /// Determines whether a path resolves inside a directory, including paths containing existing links.
    /// </summary>
    public static bool IsInsideDirectory(string path, string directory) =>
        TryResolvePathInsideDirectory(path, directory, out _);

    /// <summary>
    /// Resolves existing reparse-point components and returns the resolved path when it stays inside the directory.
    /// </summary>
    public static bool TryResolvePathInsideDirectory(string path, string directory, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (!TryResolvePath(path, out var candidate) || !TryResolvePath(directory, out var root)) return false;
        if (!IsLexicallyInsideDirectory(candidate, root)) return false;
        resolvedPath = candidate;
        return true;
    }

    /// <summary>
    /// Resolves every existing component of a path, preserving a not-yet-created trailing path.
    /// </summary>
    public static bool TryResolvePath(string path, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root)) return false;

            var current = root;
            var segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments.AsValueEnumerable())
            {
                var candidate = Path.Combine(current, segment);
                var info = new FileInfo(candidate);
                FileAttributes attributes;
                try
                {
                    attributes = info.Attributes;
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                {
                    // A missing component is the non-link tail of a path that may be created later.
                    current = candidate;
                    continue;
                }

                if (attributes == (FileAttributes)(-1))
                {
                    // FileInfo uses -1 when the path does not exist; that value must not be treated as
                    // a set of all attributes, which would incorrectly classify the path as a reparse point.
                    current = candidate;
                    continue;
                }

                if (!attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    current = candidate;
                    continue;
                }

                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) return false;

                current = Path.GetFullPath(target.FullName);
            }

            resolvedPath = Path.GetFullPath(current);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns the shared parent directory when every path has the same immediate parent.
    /// </summary>
    /// <param name="paths">The file or directory paths whose immediate parent directories are compared.</param>
    /// <returns>The normalized common parent directory, or <see langword="null"/> when the list is empty, a path is invalid, or the parents differ.</returns>
    public static string? GetCommonParentDirectory(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || GetParentDirectory(paths[0]) is not { } firstParent) return null;

        for (var i = 1; i < paths.Count; i++)
        {
            if (GetParentDirectory(paths[i]) is not { } parent || !string.Equals(parent, firstParent, GetPathComparison()))
            {
                return null;
            }
        }

        return firstParent;
    }

    private static string? GetParentDirectory(string path)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            var pathWithoutTrailingSeparator = Path.TrimEndingDirectorySeparator(fullPath);
            return Path.GetDirectoryName(pathWithoutTrailingSeparator) ?? Path.GetPathRoot(fullPath);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsLexicallyInsideDirectory(string path, string directory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return fullPath.Equals(fullDirectory, GetPathComparison()) ||
            fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, GetPathComparison());
    }

    public static StringComparison GetPathComparison()
    {
#if WINDOWS
        return StringComparison.OrdinalIgnoreCase;
#else
        return StringComparison.Ordinal;
#endif
    }
}
