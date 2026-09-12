namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Parses the strict Codex-style patch envelope without touching the filesystem.
/// </summary>
public static class PatchParser
{
    private const string BeginPatch = "*** Begin Patch";
    private const string EndPatch = "*** End Patch";
    private const string UpdateFile = "*** Update File: ";
    private const string AddFile = "*** Add File: ";
    private const string DeleteFile = "*** Delete File: ";
    private const string MoveTo = "*** Move to: ";

    /// <summary>
    /// Parses a complete patch document and rejects malformed or ambiguous operation structure.
    /// </summary>
    /// <param name="patch">The patch text to parse.</param>
    /// <returns>The parsed patch document.</returns>
    /// <exception cref="PatchParseException">Thrown when the envelope or any operation is invalid.</exception>
    public static PatchDocument Parse(string patch)
    {
        ArgumentNullException.ThrowIfNull(patch);

        var lines = NormalizeLines(patch);
        if (lines.Count < 2 || lines[0] != BeginPatch)
        {
            throw new PatchParseException(1, $"Expected '{BeginPatch}'.");
        }

        if (lines[^1] != EndPatch)
        {
            throw new PatchParseException(lines.Count, $"Expected '{EndPatch}'.");
        }

        var operations = new List<PatchFileOperation>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        var index = 1;

        while (index < lines.Count - 1)
        {
            if (TrySkipBlankSeparators(lines, ref index))
            {
                continue;
            }

            var lineNumber = index + 1;
            if (lines[index].StartsWith(UpdateFile, StringComparison.Ordinal))
            {
                var operation = ParseUpdate(lines, ref index, lineNumber);
                AddOperation(operation, operations, paths, lineNumber);
                continue;
            }

            if (lines[index].StartsWith(AddFile, StringComparison.Ordinal))
            {
                var operation = ParseAdd(lines, ref index, lineNumber);
                AddOperation(operation, operations, paths, lineNumber);
                continue;
            }

            if (lines[index].StartsWith(DeleteFile, StringComparison.Ordinal))
            {
                var operation = ParseDelete(lines, ref index, lineNumber);
                AddOperation(operation, operations, paths, lineNumber);
                continue;
            }

            if (lines[index].StartsWith(MoveTo, StringComparison.Ordinal))
            {
                throw new PatchParseException(lineNumber, $"'{MoveTo.TrimEnd()}' is only valid after an update-file header.");
            }

            throw new PatchParseException(lineNumber, "Expected a file operation header.");
        }

        if (operations.Count == 0)
        {
            throw new PatchParseException(2, "The patch must contain at least one file operation.");
        }

        return new PatchDocument(operations);
    }

    private static PatchFileOperation ParseUpdate(IReadOnlyList<string> lines, ref int index, int headerLineNumber)
    {
        var path = ParsePath(lines[index], UpdateFile, headerLineNumber);
        index++;

        string? destinationPath = null;
        if (index < lines.Count - 1 && lines[index].StartsWith(MoveTo, StringComparison.Ordinal))
        {
            destinationPath = ParsePath(lines[index], MoveTo, index + 1);
            index++;
        }

        var hunks = ParseHunks(lines, ref index, path);
        if (hunks.Count == 0 && destinationPath is null)
        {
            throw new PatchParseException(
                headerLineNumber,
                $"Update File '{path}' must contain at least one hunk beginning with '@@'.");
        }

        return destinationPath is { } destination ?
            new PatchFileOperation.Move(path, destination, hunks, headerLineNumber) :
            new PatchFileOperation.Update(path, hunks, headerLineNumber);
    }

    private static PatchFileOperation ParseAdd(IReadOnlyList<string> lines, ref int index, int headerLineNumber)
    {
        var path = ParsePath(lines[index], AddFile, headerLineNumber);
        index++;

        var content = new List<PatchLine>();
        while (index < lines.Count - 1 && !IsOperationHeader(lines[index]))
        {
            if (TrySkipBlankSeparators(lines, ref index))
            {
                break;
            }

            var line = lines[index];
            if (line.Length == 0 || line[0] != '+')
            {
                throw new PatchParseException(index + 1, "Added-file content must start with '+'.");
            }

            content.Add(new PatchLine(PatchLineKind.Add, line[1..]));
            index++;
        }

        var hunks = content.Count == 0 ?
            Array.Empty<PatchHunk>() :
            new[] { new PatchHunk(PatchHunkAnchor.Unanchored.Instance, content, false, headerLineNumber) };
        return new PatchFileOperation.Add(path, hunks, headerLineNumber);
    }

    private static PatchFileOperation ParseDelete(List<string> lines, ref int index, int headerLineNumber)
    {
        var path = ParsePath(lines[index], DeleteFile, headerLineNumber);
        index++;

        TrySkipBlankSeparators(lines, ref index);

        if (index < lines.Count - 1 && !IsOperationHeader(lines[index]))
        {
            throw new PatchParseException(index + 1, "A delete operation cannot contain patch content.");
        }

        return new PatchFileOperation.Delete(path, headerLineNumber);
    }

    private static List<PatchHunk> ParseHunks(IReadOnlyList<string> lines, ref int index, string path)
    {
        var hunks = new List<PatchHunk>();
        while (index < lines.Count - 1)
        {
            if (TrySkipBlankSeparators(lines, ref index))
            {
                continue;
            }

            var line = lines[index];
            if (IsOperationHeader(line))
            {
                break;
            }

            if (!line.StartsWith("@@", StringComparison.Ordinal))
            {
                throw new PatchParseException(
                    index + 1,
                    $"Update File '{path}' expected hunk #{hunks.Count + 1} to begin with '@@', but found '{line}'.");
            }

            var hunkNumber = hunks.Count + 1;
            var headerLineNumber = index + 1;
            var header = line;
            var anchor = ParseAnchor(header, headerLineNumber, path, hunkNumber);

            index++;
            var hunkLines = new List<PatchLine>();
            var endOfFile = false;
            var hasChange = false;

            while (index < lines.Count - 1)
            {
                line = lines[index];
                if (IsOperationHeader(line) || line.StartsWith("@@", StringComparison.Ordinal))
                {
                    break;
                }

                if (TrySkipBlankSeparators(lines, ref index))
                {
                    break;
                }

                if (line == "*** End of File")
                {
                    endOfFile = true;
                    index++;
                    break;
                }

                if (line.Length == 0)
                {
                    throw CreateHunkException(
                        index + 1,
                        path,
                        hunkNumber,
                        headerLineNumber,
                        "found a blank line inside the hunk. Prefix a semantic empty line with ' ', '+', or '-', or place separators only between hunks.");
                }

                var kind = line[0] switch
                {
                    ' ' => PatchLineKind.Context,
                    '+' => PatchLineKind.Add,
                    '-' => PatchLineKind.Remove,
                    _ => throw CreateHunkException(
                        index + 1,
                        path,
                        hunkNumber,
                        headerLineNumber,
                        $"line '{line}' must start with a space, '+', or '-'."),
                };

                hasChange |= kind is PatchLineKind.Add or PatchLineKind.Remove;
                hunkLines.Add(new PatchLine(kind, line[1..]));
                index++;
            }

            if (!hasChange)
            {
                throw CreateHunkException(
                    headerLineNumber,
                    path,
                    hunkNumber,
                    headerLineNumber,
                    $"contains no '+' or '-' lines before {DescribeBoundary(lines, index)}.");
            }

            if (anchor is PatchHunkAnchor.Unanchored &&
                !endOfFile &&
                hunkLines.AsValueEnumerable().All(static hunkLine => hunkLine.Kind is PatchLineKind.Add))
            {
                throw CreateHunkException(
                    headerLineNumber,
                    path,
                    hunkNumber,
                    headerLineNumber,
                    "is insertion-only, so a bare '@@' has no target location. Use '@@ <literal context line>' to insert after that line, or add '*** End of File' to append.");
            }

            hunks.Add(new PatchHunk(anchor, hunkLines, endOfFile, headerLineNumber));
        }

        if (hunks.Count == 0 && index < lines.Count - 1 && !IsOperationHeader(lines[index]))
        {
            throw new PatchParseException(
                index + 1,
                $"Update File '{path}' expected its first hunk to begin with '@@'.");
        }

        return hunks;
    }

    private static PatchHunkAnchor ParseAnchor(string header, int lineNumber, string path, int hunkNumber)
    {
        if (header == "@@")
        {
            return PatchHunkAnchor.Unanchored.Instance;
        }

        if (!header.StartsWith("@@ ", StringComparison.Ordinal))
        {
            throw CreateHunkException(
                lineNumber,
                path,
                hunkNumber,
                lineNumber,
                "a context header must use '@@ <literal context line>'.");
        }

        var context = header[3..];
        if (context.Length == 0)
        {
            throw CreateHunkException(lineNumber, path, hunkNumber, lineNumber, "the literal context cannot be empty.");
        }

        if (IsUnifiedDiffRangeHeader(context))
        {
            throw CreateHunkException(
                lineNumber,
                path,
                hunkNumber,
                lineNumber,
                "unified-diff range headers are not supported. Use bare '@@' or '@@ <literal source line before target>'.");
        }

        return new PatchHunkAnchor.Context(context);
    }

    private static bool IsUnifiedDiffRangeHeader(string context)
    {
        if (context[0] != '-' || !context.EndsWith(" @@", StringComparison.Ordinal))
        {
            return false;
        }

        var rangeParts = context[..^3].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return rangeParts.Length == 2 &&
            IsUnifiedDiffRangePart(rangeParts[0], '-') &&
            IsUnifiedDiffRangePart(rangeParts[1], '+');
    }

    private static bool IsUnifiedDiffRangePart(string value, char prefix)
    {
        if (value.Length < 2 || value[0] != prefix)
        {
            return false;
        }

        var numbers = value[1..].Split(',');
        return numbers.Length is 1 or 2 && numbers.All(static number => int.TryParse(number, out var parsed) && parsed >= 0);
    }

    private static PatchParseException CreateHunkException(
        int lineNumber,
        string path,
        int hunkNumber,
        int headerLineNumber,
        string message) =>
        new(
            lineNumber,
            $"Update File '{path}', hunk #{hunkNumber} (header line {headerLineNumber}): {message}");

    private static string DescribeBoundary(IReadOnlyList<string> lines, int index)
    {
        if (index >= lines.Count)
        {
            return "the end of the patch";
        }

        return $"line {index + 1} ('{lines[index]}')";
    }

    private static bool TrySkipBlankSeparators(IReadOnlyList<string> lines, ref int index)
    {
        if (index >= lines.Count || lines[index].Length != 0)
        {
            return false;
        }

        var nextIndex = index + 1;
        while (nextIndex < lines.Count && lines[nextIndex].Length == 0)
        {
            nextIndex++;
        }

        if (nextIndex >= lines.Count ||
            (!lines[nextIndex].StartsWith("@@", StringComparison.Ordinal) &&
                !IsOperationHeader(lines[nextIndex])))
        {
            return false;
        }

        index = nextIndex;
        return true;
    }

    private static void AddOperation(PatchFileOperation operation, List<PatchFileOperation> operations, HashSet<string> paths, int lineNumber)
    {
        if (!paths.Add(operation.Path))
        {
            throw new PatchParseException(lineNumber, $"The file path '{operation.Path}' occurs more than once.");
        }

        if (operation is PatchFileOperation.Move move && !paths.Add(move.DestinationPath))
        {
            throw new PatchParseException(lineNumber, $"The file path '{move.DestinationPath}' occurs more than once.");
        }

        operations.Add(operation);
    }

    private static string ParsePath(string line, string prefix, int lineNumber)
    {
        var path = line[prefix.Length..].Trim();
        if (path.Length == 0)
        {
            throw new PatchParseException(lineNumber, "A file path cannot be empty.");
        }

        return path;
    }

    private static bool IsOperationHeader(string line) =>
        line.StartsWith(UpdateFile, StringComparison.Ordinal) ||
        line.StartsWith(AddFile, StringComparison.Ordinal) ||
        line.StartsWith(DeleteFile, StringComparison.Ordinal) ||
        line.StartsWith(MoveTo, StringComparison.Ordinal) ||
        line == EndPatch;

    private static List<string> NormalizeLines(string patch)
    {
        var normalized = patch.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }
}

/// <summary>
/// Reports a structural patch parsing failure and its one-based input line.
/// </summary>
public sealed class PatchParseException(int lineNumber, string message) : FormatException($"Invalid patch at line {lineNumber}: {message}")
{
    public int LineNumber { get; } = lineNumber;
}