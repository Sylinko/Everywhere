namespace Everywhere.Common;

/// <summary>
/// Selects whether path resolution follows or preserves the final path component.
/// </summary>
public enum PathResolutionMode
{
    /// <summary>
    /// Follows links in every existing component, including the final component.
    /// </summary>
    FollowFinalComponent,

    /// <summary>
    /// Follows links in parent components while preserving the final directory entry.
    /// </summary>
    PreserveFinalComponent,
}

/// <summary>
/// Categorizes a path-resolution failure without discarding its diagnostic details.
/// </summary>
public enum PathResolutionFailureKind
{
    /// <summary>The supplied path cannot be interpreted by the current platform.</summary>
    InvalidPath,

    /// <summary>The bounded link traversal was exhausted before reaching a final path.</summary>
    TooManyLinks,

    /// <summary>An existing non-directory entry was encountered before the final component.</summary>
    NotDirectory,

    /// <summary>A link target traverses through a path component that does not exist.</summary>
    MissingComponent,

    /// <summary>The current process cannot inspect a required component.</summary>
    AccessDenied,

    /// <summary>A redirection exists but its target is unavailable through the supported .NET APIs.</summary>
    UnsupportedLink,

    /// <summary>The operating system returned another I/O failure while inspecting a component.</summary>
    IoError,
}

/// <summary>
/// Describes one link followed while resolving a path.
/// </summary>
/// <param name="Path">The resolved location of the link entry.</param>
/// <param name="LinkTarget">The raw target text stored in the link entry.</param>
/// <param name="ExpandedTargetPath">The target expanded relative to the link's containing directory.</param>
public sealed record PathLinkTransition(string Path, string LinkTarget, string ExpandedTargetPath);

/// <summary>
/// Describes why a path could not be resolved.
/// </summary>
/// <param name="Kind">The failure category.</param>
/// <param name="Path">The component being inspected when resolution failed.</param>
/// <param name="Message">The platform-provided or validation detail.</param>
/// <param name="Exception">The underlying platform exception when one was available.</param>
public sealed record PathResolutionFailure(
    PathResolutionFailureKind Kind,
    string Path,
    string Message,
    Exception? Exception = null
);

/// <summary>
/// Carries the requested path, its resolved operation path, and every followed link.
/// </summary>
/// <param name="RequestedPath">The path supplied by the caller.</param>
/// <param name="ResolvedPath">The resolved path, or <see langword="null"/> on failure.</param>
/// <param name="LinkTransitions">The links followed in resolution order.</param>
/// <param name="Failure">The failure details, or <see langword="null"/> on success.</param>
public sealed record PathResolutionResult(
    string RequestedPath,
    string? ResolvedPath,
    IReadOnlyList<PathLinkTransition> LinkTransitions,
    PathResolutionFailure? Failure
)
{
    /// <summary>
    /// Gets whether resolution produced a usable final path.
    /// </summary>
    public bool IsSuccess => ResolvedPath is not null && Failure is null;
}

/// <summary>
/// Resolves existing path components and checks containment without trusting only lexical prefixes.
/// </summary>
public static class PathUtilities
{
    /// <summary>
    /// Classifies a directory entry without dereferencing its final link.
    /// </summary>
    public enum EntryKind
    {
        Missing,
        Regular,
        Link,
        UnsupportedReparsePoint,
    }

    /// <summary>
    /// Carries a no-follow entry classification and the raw target text when the entry is a link.
    /// </summary>
    public readonly record struct EntryInspection(
        EntryKind Kind,
        FileAttributes Attributes,
        string? RawLinkTarget = null
    );

    private const int MaxLinkTransitions = 40;

    /// <summary>
    /// Gets the platform comparison used for file-system paths.
    /// </summary>
    public static StringComparison SystemPathComparison
    {
        get
        {
            // TODO: The operating-system family is only a coarse proxy for file-system comparison
            // behavior. A Unix host can mount case-insensitive volumes, and a Windows host can expose
            // file systems with different rules. Keep this policy explicit until callers can obtain a
            // reliable per-volume comparison policy without simulating file-system identity in strings.
#if WINDOWS
            return StringComparison.OrdinalIgnoreCase;
#else
            return StringComparison.Ordinal;
#endif
        }
    }

    /// <summary>
    /// Gets the platform comparer used for file-system paths.
    /// </summary>
    public static StringComparer SystemPathComparer
    {
        get
        {
#if WINDOWS
            return StringComparer.OrdinalIgnoreCase;
#else
            return StringComparer.Ordinal;
#endif
        }
    }

    /// <summary>
    /// Determines whether a path resolves inside a directory, including paths containing existing links.
    /// </summary>
    public static bool IsInsideDirectory(string path, string directory) =>
        TryResolvePathInsideDirectory(path, directory, out _);

    /// <summary>
    /// Determines whether already-resolved paths are lexically contained without resolving either path again.
    /// </summary>
    public static bool IsResolvedPathInsideDirectory(string path, string directory)
    {
        try
        {
            return IsLexicallyInsideDirectory(path, directory);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

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
        var result = ResolvePath(path, PathResolutionMode.FollowFinalComponent);
        resolvedPath = result.ResolvedPath ?? string.Empty;
        return result.IsSuccess;
    }

    /// <summary>
    /// Lexically normalizes the supplied path, then resolves its existing components and reports
    /// followed links or a detailed failure. Missing trailing components are retained so callers
    /// can apply operation-specific existence requirements.
    /// </summary>
    /// <remarks>
    /// Lexical normalization applies only to the caller's input language. Raw targets read from
    /// links are expanded and traversed component by component without normalization because their
    /// <c>.</c> and <c>..</c> segments are interpreted by the file system after preceding links.
    /// </remarks>
    /// <param name="path">An absolute or current-directory-relative path.</param>
    /// <param name="mode">Whether the final component is followed or retained as a directory entry.</param>
    /// <returns>A structured resolution result.</returns>
    public static PathResolutionResult ResolvePath(string path, PathResolutionMode mode)
    {
        var transitions = new List<PathLinkTransition>();
        var inspectedPath = path;
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Failure(PathResolutionFailureKind.InvalidPath, path, "The path cannot be empty.");
            }

            var absolutePath = Path.GetFullPath(path);
            var pendingSegments = CreateSegmentQueue(absolutePath, out var root);
            var current = root;
            string? missingTailStart = null;

            while (pendingSegments.Count > 0)
            {
                var segment = pendingSegments.Dequeue();
                if (segment is "." or "") continue;
                if (segment == "..")
                {
                    if (missingTailStart is not null)
                    {
                        return Failure(
                            PathResolutionFailureKind.MissingComponent,
                            missingTailStart,
                            "A parent traversal cannot be resolved after a missing path component. " +
                            "The file system would fail before reaching '..'.");
                    }

                    current = GetParentOrRoot(current);
                    continue;
                }

                var candidate = Path.Combine(current, segment);
                inspectedPath = candidate;
                var isFinalComponent = pendingSegments.Count == 0;
                if (missingTailStart is not null || isFinalComponent && mode is PathResolutionMode.PreserveFinalComponent)
                {
                    current = candidate;
                    continue;
                }

                var entry = InspectEntry(candidate);
                switch (entry.Kind)
                {
                    case EntryKind.Missing:
                        missingTailStart = candidate;
                        current = candidate;
                        continue;
                    case EntryKind.Regular:
                        if (pendingSegments.Count > 0 && !entry.Attributes.HasFlag(FileAttributes.Directory))
                        {
                            return Failure(
                                PathResolutionFailureKind.NotDirectory,
                                candidate,
                                "An existing non-directory path entry cannot contain another path component.");
                        }

                        current = candidate;
                        continue;
                    case EntryKind.UnsupportedReparsePoint:
                        return Failure(
                            PathResolutionFailureKind.UnsupportedLink,
                            candidate,
                            "The operating system reports a reparse point but does not expose its target through the supported .NET APIs.");
                    case EntryKind.Link:
                        break;
                    default:
                        throw new InvalidOperationException($"The path entry kind '{entry.Kind}' is not supported.");
                }

                if (transitions.Count >= MaxLinkTransitions)
                {
                    return Failure(
                        PathResolutionFailureKind.TooManyLinks,
                        candidate,
                        $"The path exceeds the limit of {MaxLinkTransitions} followed links.");
                }

                if (entry.RawLinkTarget is not { } linkTarget)
                {
                    return Failure(
                        PathResolutionFailureKind.UnsupportedLink,
                        candidate,
                        "The link target is unavailable through the supported .NET APIs.");
                }

                var targetPath = ExpandRawLinkTarget(candidate, linkTarget);
                transitions.Add(new PathLinkTransition(candidate, linkTarget, targetPath));
                var remainingSegments = pendingSegments.ToArray();
                pendingSegments = CreateSegmentQueue(targetPath, out root);
                foreach (var remainingSegment in remainingSegments) pendingSegments.Enqueue(remainingSegment);
                current = root;
                missingTailStart = null;
            }

            return new PathResolutionResult(path, Path.GetFullPath(current), [.. transitions], null);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failure(PathResolutionFailureKind.AccessDenied, inspectedPath, ex.Message, ex);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Failure(PathResolutionFailureKind.InvalidPath, inspectedPath, ex.Message, ex);
        }
        catch (IOException ex)
        {
            return Failure(PathResolutionFailureKind.IoError, inspectedPath, ex.Message, ex);
        }

        PathResolutionResult Failure(PathResolutionFailureKind kind, string failedPath, string message, Exception? exception = null) =>
            new(path, null, [.. transitions], new PathResolutionFailure(kind, failedPath, message, exception));
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
            if (GetParentDirectory(paths[i]) is not { } parent || !string.Equals(parent, firstParent, SystemPathComparison))
            {
                return null;
            }
        }

        return firstParent;
    }

    /// <summary>
    /// Expands environment variables and lexically normalizes a local input path against an
    /// absolute base directory. This does not inspect the file system or resolve links.
    /// </summary>
    internal static string ExpandFullPath(string path, string baseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseDirectory);
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), baseDirectory);
    }

    /// <summary>
    /// Returns whether a path starts with a URI-shaped scheme and <c>://</c> delimiter.
    /// </summary>
    internal static bool HasExplicitUriScheme(string path)
    {
        var separatorIndex = path.IndexOf("://", StringComparison.Ordinal);
        return separatorIndex > 0 && Uri.CheckSchemeName(path[..separatorIndex]);
    }

    /// <summary>
    /// Inspects one directory entry without following a link stored in that final component.
    /// </summary>
    public static EntryInspection InspectEntry(string path)
    {
        var info = new FileInfo(path);
        string? linkTarget;
        try
        {
            // Query LinkTarget before attributes because it remains available for dangling links,
            // while attribute APIs commonly report those entries as missing.
            linkTarget = info.LinkTarget;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EntryInspection(EntryKind.Missing, (FileAttributes)(-1));
        }

        if (linkTarget is not null)
        {
            return new EntryInspection(EntryKind.Link, FileAttributes.ReparsePoint, linkTarget);
        }

        FileAttributes attributes;
        try
        {
            attributes = info.Attributes;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new EntryInspection(EntryKind.Missing, (FileAttributes)(-1));
        }

        if (attributes == (FileAttributes)(-1))
        {
            return new EntryInspection(EntryKind.Missing, attributes);
        }

        return new EntryInspection(
            attributes.HasFlag(FileAttributes.ReparsePoint) ? EntryKind.UnsupportedReparsePoint : EntryKind.Regular,
            attributes);
    }

    private static string ExpandRawLinkTarget(string linkPath, string linkTarget)
    {
        if (Path.IsPathFullyQualified(linkTarget)) return linkTarget;

        var containingDirectory = Path.GetDirectoryName(linkPath) ?? throw new IOException($"The link '{linkPath}' does not have a containing directory.");
        if (!Path.IsPathRooted(linkTarget)) return Path.Combine(containingDirectory, linkTarget);

        var root = Path.GetPathRoot(linkPath) ?? throw new IOException($"The link '{linkPath}' does not have a filesystem root.");
        return Path.Combine(root, linkTarget.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static Queue<string> CreateSegmentQueue(string path, out string root)
    {
        root = Path.GetPathRoot(path) ?? throw new ArgumentException("The path does not have a file-system root.", nameof(path));
        var separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar ?
            new[] { Path.DirectorySeparatorChar } :
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return new Queue<string>(path[root.Length..].Split(separators, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string GetParentOrRoot(string path)
    {
        var pathWithoutTrailingSeparator = Path.TrimEndingDirectorySeparator(path);
        return Path.GetDirectoryName(pathWithoutTrailingSeparator) ?? Path.GetPathRoot(path) ?? path;
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
        if (fullPath.Equals(fullDirectory, SystemPathComparison)) return true;

        var directoryPrefix = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(directoryPrefix, SystemPathComparison);
    }
}