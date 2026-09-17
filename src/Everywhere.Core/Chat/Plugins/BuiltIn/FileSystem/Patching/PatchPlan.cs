using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Bounds planning and output growth before any filesystem mutation is allowed.
/// </summary>
/// <param name="MaxFiles">Maximum number of file operations.</param>
/// <param name="MaxHunks">Maximum number of update hunks.</param>
/// <param name="MaxFileBytes">Maximum source file size loaded into memory.</param>
/// <param name="MaxOutputBytes">Maximum encoded output size for one file.</param>
/// <param name="MaxGrowthRatio">Maximum output-to-input character ratio for existing files.</param>
/// <param name="MaxChangedLines">Maximum estimated changed logical lines.</param>
public readonly record struct PatchLimits(
    int MaxFiles,
    int MaxHunks,
    long MaxFileBytes,
    long MaxOutputBytes,
    double MaxGrowthRatio,
    int MaxChangedLines
)
{
    public static PatchLimits Default => new(
        MaxFiles: 32,
        MaxHunks: 256,
        MaxFileBytes: 50L * 1024 * 1024,
        MaxOutputBytes: 50L * 1024 * 1024,
        MaxGrowthRatio: 10,
        MaxChangedLines: 100_000);
}

/// <summary>
/// Contains immutable per-file patch plans produced from one parsed document.
/// </summary>
public sealed class PatchPlan(IReadOnlyList<PatchPlanFile> files)
{
    public IReadOnlyList<PatchPlanFile> Files { get; } = files;
}

/// <summary>
/// Describes one hunk that required tolerant text matching while building a patch plan.
/// </summary>
public sealed record PatchMatchDiagnostic(int HunkNumber, int HeaderLineNumber, PatchMatchKind Kind);

/// <summary>
/// Contains the original snapshot and proposed logical content for one planned operation.
/// </summary>
public abstract class PatchPlanFile
{
    public required string RequestedSourcePath { get; init; }

    public required string SourcePath { get; init; }

    public required PathResolutionResult SourceResolution { get; init; }

    public required PatchTextFileSnapshot Original { get; init; }

    public required string ProposedContent { get; init; }

    public IReadOnlyList<PatchMatchDiagnostic> MatchDiagnostics { get; init; } = [];

    public abstract string ReviewPath { get; }

    public bool HasContentChange => !string.Equals(Original.Content, ProposedContent, StringComparison.Ordinal);

    /// <summary>
    /// Converts the planned change into the existing review model.
    /// </summary>
    public abstract TextDifference CreateDifference();
}

/// <summary>
/// Contains a planned new-file operation and its encoded content.
/// </summary>
public sealed class PatchAddPlanFile : PatchPlanFile
{
    public required byte[] ProposedBytes { get; init; }

    public override string ReviewPath => SourcePath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        if (ProposedContent.Length > 0)
        {
            difference.AddRange(TextChange.Insert(0, ProposedContent));
        }

        return difference;
    }
}

/// <summary>
/// Contains a planned update operation and its encoded content.
/// </summary>
public sealed class PatchUpdatePlanFile : PatchPlanFile
{
    public required string SourceEntryPath { get; init; }

    public required PathResolutionResult SourceEntryResolution { get; init; }

    public required byte[] ProposedBytes { get; init; }

    public override string ReviewPath => SourcePath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        TextDifferenceBuilder.BuildLineDiff(difference, Original.Content, ProposedContent);
        return difference;
    }
}

/// <summary>
/// Contains a planned delete operation.
/// </summary>
public sealed class PatchDeletePlanFile : PatchPlanFile
{
    public PatchLinkSnapshot? SourceLink { get; init; }

    public override string ReviewPath => SourcePath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        if (Original.Content.Length > 0)
        {
            difference.AddRange(TextChange.Delete(0, Original.Content.Length));
        }

        return difference;
    }
}

/// <summary>
/// Contains a planned move operation and its encoded destination content.
/// </summary>
public sealed class PatchMovePlanFile : PatchPlanFile
{
    public required string RequestedDestinationPath { get; init; }

    public required string ContentPath { get; init; }

    public required PathResolutionResult ContentResolution { get; init; }

    public required string DestinationPath { get; init; }

    public required PathResolutionResult DestinationResolution { get; init; }

    public PatchLinkSnapshot? SourceLink { get; init; }

    public required byte[] ProposedBytes { get; init; }

    public override string ReviewPath => DestinationPath;

    public override TextDifference CreateDifference()
    {
        var difference = new TextDifference(ReviewPath);
        if (HasContentChange)
        {
            TextDifferenceBuilder.BuildLineDiff(difference, Original.Content, ProposedContent);
        }

        return difference;
    }
}

/// <summary>
/// Captures the identity of a final link entry without reading its referent content.
/// </summary>
/// <param name="Target">The raw link target stored in the directory entry.</param>
public sealed record PatchLinkSnapshot(string Target);

/// <summary>
/// Resolves all paths, snapshots all sources, locates hunks, and builds a mutation-free plan.
/// </summary>
public static class PatchPlanBuilder
{
    /// <summary>
    /// Builds a complete multi-file plan before any target is written.
    /// </summary>
    /// <param name="document">The parsed patch document.</param>
    /// <param name="workingDirectory">The base directory for relative patch paths.</param>
    /// <param name="limits">Safety limits for the planning operation.</param>
    /// <param name="cancellationToken">Cancels planning and file reads.</param>
    /// <returns>A complete immutable patch plan.</returns>
    public static async ValueTask<PatchPlan> BuildAsync(
        PatchDocument document,
        string workingDirectory,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            throw new PatchPlanException("The patch working directory cannot be empty.");
        }

        if (document.Operations.Count == 0 || document.Operations.Count > limits.MaxFiles)
        {
            throw new PatchPlanException($"The patch contains {document.Operations.Count} files; the maximum is {limits.MaxFiles}.");
        }

        var totalHunks = document.Operations.AsValueEnumerable().Sum(operation => operation switch
        {
            PatchFileOperation.Add add => add.Hunks.Count,
            PatchFileOperation.Update update => update.Hunks.Count,
            PatchFileOperation.Delete => 0,
            PatchFileOperation.Move move => move.Hunks.Count,
            _ => throw new PatchPlanException($"The patch operation type '{operation.GetType().Name}' is not supported.")
        });
        if (totalHunks > limits.MaxHunks)
        {
            throw new PatchPlanException($"The patch contains {totalHunks} hunks; the maximum is {limits.MaxHunks}.");
        }

        // Planning intentionally snapshots every source before commit, so a later operation that
        // reads a path written by an earlier operation observes the initial state. TODO: Define and
        // validate cross-operation read/write dependencies before changing this to ordered semantics
        // or rejecting all such overlaps.
        var paths = new HashSet<string>(PathUtilities.SystemPathComparer);
        var plannedFiles = new List<PatchPlanFile>(document.Operations.Count);

        foreach (var operation in document.Operations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                switch (operation)
                {
                    case PatchFileOperation.Add add:
                    {
                        var sourceResolution = ResolveOperationPath(
                            workingDirectory,
                            add,
                            add.Path,
                            PathResolutionMode.PreserveFinalComponent,
                            "destination");
                        var sourcePath = GetResolvedPath(sourceResolution);
                        AddPath(sourcePath, paths);
                        EnsureParentDirectory(sourcePath);
                        EnsureMissingFile(sourcePath);
                        plannedFiles.Add(PlanAdd(add, sourceResolution, limits));
                        break;
                    }
                    case PatchFileOperation.Update update:
                    {
                        var sourceEntryResolution = ResolveOperationPath(
                            workingDirectory,
                            update,
                            update.Path,
                            PathResolutionMode.PreserveFinalComponent,
                            "source entry");
                        var sourceResolution = ResolveOperationPath(
                            workingDirectory,
                            update,
                            update.Path,
                            PathResolutionMode.FollowFinalComponent,
                            "source");
                        var sourcePath = GetResolvedPath(sourceResolution);
                        AddPath(sourcePath, paths);
                        EnsureParentDirectory(sourcePath);
                        plannedFiles.Add(await PlanUpdateAsync(
                            update,
                            sourceEntryResolution,
                            sourceResolution,
                            limits,
                            cancellationToken));
                        break;
                    }
                    case PatchFileOperation.Delete delete:
                    {
                        var sourceResolution = ResolveOperationPath(
                            workingDirectory,
                            delete,
                            delete.Path,
                            PathResolutionMode.PreserveFinalComponent,
                            "source entry");
                        var sourcePath = GetResolvedPath(sourceResolution);
                        AddPath(sourcePath, paths);
                        EnsureParentDirectory(sourcePath);
                        plannedFiles.Add(await PlanDeleteAsync(delete, sourceResolution, limits, cancellationToken));
                        break;
                    }
                    case PatchFileOperation.Move move:
                    {
                        var sourceResolution = ResolveOperationPath(
                            workingDirectory,
                            move,
                            move.Path,
                            PathResolutionMode.PreserveFinalComponent,
                            "source entry");
                        var contentResolution = ResolveOperationPath(
                            workingDirectory,
                            move,
                            move.Path,
                            PathResolutionMode.FollowFinalComponent,
                            "content source");
                        var destinationResolution = ResolveOperationPath(
                            workingDirectory,
                            move,
                            move.DestinationPath,
                            PathResolutionMode.PreserveFinalComponent,
                            "destination");
                        var sourcePath = GetResolvedPath(sourceResolution);
                        var destinationPath = GetResolvedPath(destinationResolution);
                        AddPath(sourcePath, paths);
                        AddPath(destinationPath, paths);
                        EnsureParentDirectory(sourcePath);
                        EnsureParentDirectory(destinationPath);
                        EnsureMissingFile(destinationPath);
                        plannedFiles.Add(
                            await PlanMoveAsync(
                                move,
                                sourceResolution,
                                contentResolution,
                                destinationResolution,
                                limits,
                                cancellationToken));
                        break;
                    }
                    default:
                        throw new PatchPlanException($"The patch operation type '{operation.GetType().Name}' is not supported.");
                }
            }
            catch (PatchPlanException ex) when (!ex.HasOperationContext)
            {
                throw CreateOperationException(operation, ex);
            }
        }

        return new PatchPlan(plannedFiles);
    }

    private static PatchAddPlanFile PlanAdd(
        PatchFileOperation.Add operation,
        PathResolutionResult sourceResolution,
        PatchLimits limits)
    {
        var path = GetResolvedPath(sourceResolution);
        var original = PatchTextFileSnapshot.CreateNew(path);
        var lines = operation.Hunks.Count == 0 ?
            Array.Empty<PatchSourceLine>() :
            operation.Hunks.AsValueEnumerable().Single().Lines.Select(line => new PatchSourceLine(line.Text, string.Empty)).ToArray();
        var proposedContent = PatchTextFileSnapshot.RenderLines(
            NormalizeLineEndings(lines, original, original.EndsWithLineEnding));
        var proposedBytes = original.Encode(proposedContent);
        EnsureOutputBudget(path, original.Content, proposedContent, proposedBytes.Length, limits);

        return new PatchAddPlanFile
        {
            RequestedSourcePath = operation.Path,
            SourcePath = path,
            SourceResolution = sourceResolution,
            Original = original,
            ProposedContent = proposedContent,
            ProposedBytes = proposedBytes
        };
    }

    private static async ValueTask<PatchUpdatePlanFile> PlanUpdateAsync(
        PatchFileOperation.Update operation,
        PathResolutionResult sourceEntryResolution,
        PathResolutionResult sourceResolution,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var sourcePath = GetResolvedPath(sourceResolution);
        var original = await ReadSnapshotAsync(operation, sourceResolution, "source", limits.MaxFileBytes, cancellationToken);
        var application = operation.Hunks.Count == 0 ? new PatchHunkApplication(original.Content, []) : ApplyHunks(original, operation.Hunks);
        var proposedBytes = original.Encode(application.Content);
        EnsureOutputBudget(sourcePath, original.Content, application.Content, proposedBytes.Length, limits);

        return new PatchUpdatePlanFile
        {
            RequestedSourcePath = operation.Path,
            SourcePath = sourcePath,
            SourceResolution = sourceResolution,
            SourceEntryPath = GetResolvedPath(sourceEntryResolution),
            SourceEntryResolution = sourceEntryResolution,
            Original = original,
            ProposedContent = application.Content,
            ProposedBytes = proposedBytes,
            MatchDiagnostics = application.Diagnostics
        };
    }

    private static async ValueTask<PatchMovePlanFile> PlanMoveAsync(
        PatchFileOperation.Move operation,
        PathResolutionResult sourceResolution,
        PathResolutionResult contentResolution,
        PathResolutionResult destinationResolution,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var sourcePath = GetResolvedPath(sourceResolution);
        var contentPath = GetResolvedPath(contentResolution);
        var destinationPath = GetResolvedPath(destinationResolution);
        var sourceLink = ReadFinalLinkSnapshot(sourcePath);
        var original = await ReadSnapshotAsync(operation, contentResolution, "content source", limits.MaxFileBytes, cancellationToken);
        var application = operation.Hunks.Count == 0 ? new PatchHunkApplication(original.Content, []) : ApplyHunks(original, operation.Hunks);
        var proposedBytes = original.Encode(application.Content);
        EnsureOutputBudget(contentPath, original.Content, application.Content, proposedBytes.Length, limits);

        return new PatchMovePlanFile
        {
            RequestedSourcePath = operation.Path,
            SourcePath = sourcePath,
            SourceResolution = sourceResolution,
            RequestedDestinationPath = operation.DestinationPath,
            ContentPath = contentPath,
            ContentResolution = contentResolution,
            DestinationPath = destinationPath,
            DestinationResolution = destinationResolution,
            SourceLink = sourceLink,
            Original = original,
            ProposedContent = application.Content,
            ProposedBytes = proposedBytes,
            MatchDiagnostics = application.Diagnostics
        };
    }

    private static async ValueTask<PatchDeletePlanFile> PlanDeleteAsync(
        PatchFileOperation.Delete operation,
        PathResolutionResult sourceResolution,
        PatchLimits limits,
        CancellationToken cancellationToken)
    {
        var path = GetResolvedPath(sourceResolution);
        var sourceLink = ReadFinalLinkSnapshot(path);
        var original = sourceLink is null ?
            await ReadSnapshotAsync(operation, sourceResolution, "source entry", limits.MaxFileBytes, cancellationToken) :
            PatchTextFileSnapshot.CreateNew(path);
        return new PatchDeletePlanFile
        {
            RequestedSourcePath = operation.Path,
            SourcePath = path,
            SourceResolution = sourceResolution,
            SourceLink = sourceLink,
            Original = original,
            ProposedContent = string.Empty,
        };
    }

    private static PatchHunkApplication ApplyHunks(PatchTextFileSnapshot original, IReadOnlyList<PatchHunk> hunks)
    {
        var originalLines = original.Lines.AsValueEnumerable().Select(static line => line.Text).ToArray();
        var locatedMatches = new List<(PatchHunk Hunk, PatchHunkMatch Match)>(hunks.Count);
        var searchStartIndex = 0;
        PatchHunkMatch? previousMatch = null;
        for (var hunkIndex = 0; hunkIndex < hunks.Count; hunkIndex++)
        {
            var hunk = hunks[hunkIndex];
            var match = LocateHunkInOrder(
                original.Path,
                originalLines,
                hunk,
                hunkIndex + 1,
                searchStartIndex,
                previousMatch);
            locatedMatches.Add((hunk, match));
            searchStartIndex = match.EndIndex;
            previousMatch = match;
        }

        EnsurePatchOrder(locatedMatches);
        var matches = locatedMatches.AsValueEnumerable().OrderBy(item => item.Match.StartIndex).ToArray();
        EnsureNonOverlapping(matches);

        var lines = original.Lines.AsValueEnumerable().ToList();
        foreach (var item in matches.AsValueEnumerable().Reverse())
        {
            var replacement = BuildReplacement(original, item.Hunk, item.Match.StartIndex, item.Match.EndIndex);
            lines.RemoveRange(item.Match.StartIndex, item.Match.EndIndex - item.Match.StartIndex);
            lines.InsertRange(item.Match.StartIndex, replacement);
        }

        var diagnostics = locatedMatches
            .AsValueEnumerable()
            .Select((item, index) => new PatchMatchDiagnostic(index + 1, item.Hunk.HeaderLineNumber, item.Match.Kind))
            .Where(static diagnostic => diagnostic.Kind is not PatchMatchKind.Exact and not PatchMatchKind.Context)
            .ToArray();
        var content = PatchTextFileSnapshot.RenderLines(
            NormalizeLineEndings(lines, original, original.EndsWithLineEnding));
        return new PatchHunkApplication(content, diagnostics);
    }

    private static PatchHunkMatch LocateHunkInOrder(
        string path,
        IReadOnlyList<string> originalLines,
        PatchHunk hunk,
        int hunkNumber,
        int searchStartIndex,
        PatchHunkMatch? previousMatch)
    {
        try
        {
            return PatchHunkMatcher.Locate(originalLines, hunk, searchStartIndex);
        }
        catch (PatchMatchException exception)
        {
            var earlierMatch = searchStartIndex > 0 ? TryLocateFromStart(originalLines, hunk) : null;
            if (earlierMatch is { } earlier && earlier.StartIndex < searchStartIndex)
            {
                var message = previousMatch is { } previous && earlier.EndIndex > previous.StartIndex ?
                    "overlaps a previous hunk." :
                    "is out of order; its target occurs before the previous hunk ended.";
                throw CreateHunkMatchException(path, hunk, hunkNumber, message, exception);
            }

            throw CreateHunkMatchException(path, hunk, hunkNumber, exception.Message, exception);
        }
    }

    private static PatchHunkMatch? TryLocateFromStart(IReadOnlyList<string> originalLines, PatchHunk hunk)
    {
        try
        {
            return PatchHunkMatcher.Locate(originalLines, hunk);
        }
        catch (PatchMatchException)
        {
            return null;
        }
    }

    private static PatchMatchException CreateHunkMatchException(
        string path,
        PatchHunk hunk,
        int hunkNumber,
        string message,
        Exception innerException) =>
        new($"Patch target '{path}', hunk #{hunkNumber} (patch header line {hunk.HeaderLineNumber}): {message}", innerException);

    private static void EnsurePatchOrder(List<(PatchHunk Hunk, PatchHunkMatch Match)> matches)
    {
        for (var index = 1; index < matches.Count; index++)
        {
            if (matches[index - 1].Match.StartIndex > matches[index].Match.StartIndex)
            {
                throw new PatchMatchException("Patch hunks are out of order; their locations must follow patch order.");
            }
        }
    }

    private static List<PatchSourceLine> BuildReplacement(PatchTextFileSnapshot original, PatchHunk hunk, int startIndex, int endIndex)
    {
        var sourceLines = original.Lines.AsValueEnumerable().Skip(startIndex).Take(endIndex - startIndex).ToArray();
        var sourceIndex = 0;
        var replacement = new List<PatchSourceLine>(hunk.Lines.Count);
        foreach (var line in hunk.Lines.AsValueEnumerable())
        {
            if (line.Kind is PatchLineKind.Add)
            {
                replacement.Add(new PatchSourceLine(line.Text, string.Empty));
                continue;
            }

            if (sourceIndex >= sourceLines.Length)
            {
                throw new PatchMatchException("The hunk match changed while constructing the replacement.");
            }

            if (line.Kind is PatchLineKind.Context) replacement.Add(sourceLines[sourceIndex]);
            sourceIndex++;
        }

        if (sourceIndex != sourceLines.Length)
        {
            throw new PatchMatchException("The hunk match did not consume the complete matched range.");
        }

        return replacement;
    }

    private sealed record PatchHunkApplication(string Content, IReadOnlyList<PatchMatchDiagnostic> Diagnostics);

    private static PatchSourceLine[] NormalizeLineEndings(
        IReadOnlyList<PatchSourceLine> lines,
        PatchTextFileSnapshot original,
        bool originalEndsWithLineEnding)
    {
        if (lines.Count == 0) return [];

        var normalized = lines.ToArray();
        for (var index = 0; index < normalized.Length - 1; index++)
        {
            if (normalized[index].LineEnding.Length == 0)
            {
                normalized[index] = normalized[index] with { LineEnding = original.DefaultLineEnding };
            }
        }

        var last = normalized[^1];
        if (last.LineEnding.Length == 0 && originalEndsWithLineEnding)
        {
            normalized[^1] = last with { LineEnding = original.DefaultLineEnding };
        }

        return normalized;
    }

    private static void EnsureNonOverlapping((PatchHunk Hunk, PatchHunkMatch Match)[] matches)
    {
        for (var index = 1; index < matches.Length; index++)
        {
            var previous = matches[index - 1].Match;
            var current = matches[index].Match;
            var overlaps = previous.EndIndex > current.StartIndex ||
                previous.StartIndex == previous.EndIndex && current.StartIndex == current.EndIndex &&
                previous.StartIndex == current.StartIndex;
            if (overlaps)
            {
                throw new PatchMatchException("Patch hunks overlap or target the same insertion position.");
            }
        }
    }

    private static PathResolutionResult ResolveOperationPath(
        string workingDirectory,
        PatchFileOperation operation,
        string path,
        PathResolutionMode mode,
        string role)
    {
        if (PathUtilities.HasExplicitUriScheme(path))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                throw new PatchPlanException($"The patch path '{path}' is not a valid absolute URI.");
            }

            if (!uri.IsFile)
            {
                throw new PatchPlanException($"The patch path '{path}' uses an unsupported URI scheme.");
            }

            path = uri.LocalPath;
        }

        string requestedPath;
        try
        {
            requestedPath = PathUtilities.ExpandFullPath(path, workingDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new PatchPlanException(
                $"{DescribeOperation(operation)} has an invalid {role} path '{path}': {ex.Message}",
                ex,
                hasOperationContext: true);
        }

        var result = PathUtilities.ResolvePath(requestedPath, mode);
        if (result.IsSuccess) return result;

        if (result.Failure is not { } failure)
        {
            throw new PatchPlanException(
                $"{DescribeOperation(operation)} could not resolve its {role} path '{path}', but no failure detail was provided.",
                hasOperationContext: true);
        }
        var transitions = FormatLinkTransitions(result.LinkTransitions);
        throw new PatchPlanException(
            $"{DescribeOperation(operation)} could not resolve its {role} path '{path}'. " +
            $"Failed at '{failure.Path}' ({failure.Kind}): {failure.Message}{transitions}",
            hasOperationContext: true);
    }

    private static void AddPath(string path, HashSet<string> paths)
    {
        if (!paths.Add(path))
        {
            throw new PatchPlanException($"The patch resolves more than one operation to '{path}'.");
        }
    }

    private static void EnsureParentDirectory(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new PatchPlanException($"The parent directory for '{path}' does not exist.");
        }
    }

    private static void EnsureMissingFile(string path)
    {
        try
        {
            var entry = PathUtilities.InspectEntry(path);
            switch (entry.Kind)
            {
                case PathUtilities.EntryKind.Missing:
                    return;
                case PathUtilities.EntryKind.Regular:
                    throw new PatchPlanException($"The patch destination '{path}' already exists.");
                case PathUtilities.EntryKind.Link:
                    throw new PatchPlanException(
                        $"The patch destination '{path}' is occupied by a link whose raw target is '{entry.RawLinkTarget}'.");
                case PathUtilities.EntryKind.UnsupportedReparsePoint:
                    throw new PatchPlanException(
                        $"The patch destination '{path}' is occupied by a reparse point whose target cannot be inspected on this platform.");
                default:
                    throw new InvalidOperationException($"The path entry kind '{entry.Kind}' is not supported.");
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PatchPlanException($"Access to patch destination '{path}' was denied while checking whether it exists: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new PatchPlanException($"The patch destination '{path}' could not be inspected: {ex.Message}", ex);
        }
    }

    private static PatchLinkSnapshot? ReadFinalLinkSnapshot(string path)
    {
        try
        {
            var entry = PathUtilities.InspectEntry(path);
            return entry.Kind switch
            {
                PathUtilities.EntryKind.Regular => null,
                PathUtilities.EntryKind.Link => new PatchLinkSnapshot(entry.RawLinkTarget ??
                    throw new PatchPlanException($"The patch source link '{path}' did not expose its raw target.")),
                PathUtilities.EntryKind.Missing => throw new PatchPlanException($"The patch source entry '{path}' does not exist."),
                PathUtilities.EntryKind.UnsupportedReparsePoint => throw new PatchPlanException(
                    $"The patch source entry '{path}' is a reparse point whose target cannot be inspected on this platform."),
                _ => throw new InvalidOperationException($"The path entry kind '{entry.Kind}' is not supported.")
            };
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new PatchPlanException($"Access to patch source entry '{path}' was denied while inspecting its link target: {ex.Message}", ex);
        }
        catch (IOException ex)
        {
            throw new PatchPlanException($"The patch source entry '{path}' could not be inspected: {ex.Message}", ex);
        }
    }

    private static PatchPlanException CreateOperationException(PatchFileOperation operation, PatchPlanException exception) =>
        new($"{DescribeOperation(operation)} could not be planned. {exception.Message}", exception, hasOperationContext: true);

    private static async ValueTask<PatchTextFileSnapshot> ReadSnapshotAsync(
        PatchFileOperation operation,
        PathResolutionResult resolution,
        string role,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            return await PatchTextFileSnapshot.ReadAsync(GetResolvedPath(resolution), maxBytes, cancellationToken);
        }
        catch (PatchPlanException ex)
        {
            throw new PatchPlanException(
                $"{DescribeOperation(operation)} resolved its {role} path to '{resolution.ResolvedPath}', but it could not be read. " +
                $"{ex.Message}{FormatLinkTransitions(resolution.LinkTransitions)}",
                ex,
                hasOperationContext: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PatchPlanException(
                $"{DescribeOperation(operation)} resolved its {role} path to '{resolution.ResolvedPath}', but it could not be read: {ex.Message}" +
                FormatLinkTransitions(resolution.LinkTransitions),
                ex,
                hasOperationContext: true);
        }
    }

    private static string DescribeOperation(PatchFileOperation operation)
    {
        var kind = operation switch
        {
            PatchFileOperation.Add => "Add File",
            PatchFileOperation.Update => "Update File",
            PatchFileOperation.Delete => "Delete File",
            PatchFileOperation.Move => "Move File",
            _ => "File operation"
        };
        return $"{kind} '{operation.Path}' (patch header line {operation.HeaderLineNumber})";
    }

    private static string FormatLinkTransitions(IReadOnlyList<PathLinkTransition> transitions) => transitions.Count == 0 ?
        string.Empty :
        " Links followed: " + string.Join(", ", transitions.Select(static transition =>
            $"'{transition.Path}' -> '{transition.LinkTarget}' (expanded to '{transition.ExpandedTargetPath}')")) + ".";

    private static string GetResolvedPath(PathResolutionResult resolution) =>
        resolution.ResolvedPath ?? throw new PatchPlanException(
            $"Path resolution for '{resolution.RequestedPath}' did not produce a final path.");

    /// <summary>
    /// Validates the encoded size, growth ratio, and estimated line changes of proposed content.
    /// </summary>
    public static void EnsureOutputBudget(
        string path,
        string originalContent,
        string proposedContent,
        int proposedBytes,
        PatchLimits limits)
    {
        if (proposedBytes > limits.MaxOutputBytes)
        {
            throw new PatchPlanException($"The patched file '{path}' exceeds the output size limit of {limits.MaxOutputBytes} bytes.");
        }

        var growthRatio = originalContent.Length == 0 ? 1 : (double)proposedContent.Length / originalContent.Length;
        if (growthRatio > limits.MaxGrowthRatio)
        {
            throw new PatchPlanException($"The patched file '{path}' exceeds the maximum growth ratio of {limits.MaxGrowthRatio}.");
        }

        var changedLines = CountChangedLines(originalContent, proposedContent);
        if (changedLines > limits.MaxChangedLines)
        {
            throw new PatchPlanException($"The patch changes {changedLines} lines in '{path}'; the maximum is {limits.MaxChangedLines}.");
        }
    }

    private static int CountChangedLines(string original, string proposed)
    {
        var changes = TextDifferenceBuilder.BuildLineChanges(original, proposed);
        return changes.Sum(change =>
            change.Kind is TextChangeKind.Replace ?
                Math.Max(
                    TextDifference.CountLines(change.GetOriginalSlice(original)),
                    TextDifference.CountLines(change.NewText ?? string.Empty)) :
                TextDifference.CountLines(
                    change.Kind is TextChangeKind.Delete ? change.GetOriginalSlice(original) : change.NewText ?? string.Empty));
    }
}

/// <summary>
/// Reports a patch that cannot be safely planned from the requested paths or contents.
/// </summary>
public sealed class PatchPlanException(
    string message,
    Exception? innerException = null,
    bool hasOperationContext = false
) : InvalidOperationException(message, innerException)
{
    public bool HasOperationContext { get; } = hasOperationContext;
}