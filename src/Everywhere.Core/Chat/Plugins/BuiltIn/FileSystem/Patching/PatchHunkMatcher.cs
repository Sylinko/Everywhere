namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem.Patching;

/// <summary>
/// Locates patch hunks against one immutable logical-line snapshot.
/// </summary>
public static class PatchHunkMatcher
{
    /// <summary>
    /// Finds the target range for a hunk and fails closed when its declared location cannot be resolved safely.
    /// </summary>
    /// <param name="originalLines">The target file's logical lines.</param>
    /// <param name="hunk">The hunk to locate.</param>
    /// <param name="searchStartIndex">The first source line that may be considered.</param>
    /// <returns>The zero-based half-open match range.</returns>
    /// <exception cref="PatchMatchException">Thrown when the hunk cannot be located safely.</exception>
    public static PatchHunkMatch Locate(IReadOnlyList<string> originalLines, PatchHunk hunk, int searchStartIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(originalLines);
        ArgumentNullException.ThrowIfNull(hunk);
        if (searchStartIndex < 0 || searchStartIndex > originalLines.Count)
        {
            throw new PatchMatchException("The hunk search position is outside the target file.");
        }

        var expectedLines = hunk.Lines
            .AsValueEnumerable()
            .Where(line => line.Kind is not PatchLineKind.Add)
            .Select(line => line.Text)
            .ToArray();

        return hunk.Anchor switch
        {
            PatchHunkAnchor.Unanchored => LocateUnanchored(originalLines, hunk, expectedLines, searchStartIndex),
            PatchHunkAnchor.Context context => LocateContext(originalLines, hunk, context.Text, expectedLines, searchStartIndex),
            _ => throw new PatchMatchException("The hunk uses an unsupported source-location anchor."),
        };
    }

    private static PatchHunkMatch LocateUnanchored(IReadOnlyList<string> originalLines, PatchHunk hunk, string[] expectedLines, int searchStartIndex)
    {
        if (expectedLines.Length == 0)
        {
            if (!hunk.EndOfFile)
            {
                throw new PatchMatchException(
                    "An insertion-only hunk with a bare '@@' has no target location. Use '@@ <literal context line>' to insert after that line, or add '*** End of File' to append.");
            }

            var insertionIndex = originalLines.Count;
            return new PatchHunkMatch(insertionIndex, insertionIndex, PatchMatchKind.Exact);
        }

        return LocateNextSequence(originalLines, hunk, expectedLines, searchStartIndex);
    }

    private static PatchHunkMatch LocateContext(
        IReadOnlyList<string> originalLines,
        PatchHunk hunk,
        string context,
        string[] expectedLines,
        int searchStartIndex)
    {
        var anchorMatch = SeekSequence(originalLines, [context], searchStartIndex, endOfFile: false);
        if (anchorMatch is not { } anchor)
        {
            throw new PatchMatchException("The hunk context anchor does not match the target file.");
        }

        if (expectedLines.Length == 0)
        {
            var insertionIndex = hunk.EndOfFile ? originalLines.Count : anchor.EndIndex;
            return new PatchHunkMatch(
                insertionIndex,
                insertionIndex,
                ToPatchMatchKind(anchor.Kind, usesContextAnchor: true));
        }

        var targetMatch = SeekSequence(originalLines, expectedLines, anchor.EndIndex, hunk.EndOfFile);
        if (targetMatch is not { } target)
        {
            ThrowIfOnlyNonEndOfFileMatchExists(originalLines, hunk, expectedLines, anchor.EndIndex);
            throw new PatchMatchException("The hunk context does not match the target file after the requested context anchor.");
        }

        EnsureEndOfFile(hunk, target.EndIndex, originalLines.Count);
        var kind = (int)anchor.Kind > (int)target.Kind ? anchor.Kind : target.Kind;
        return new PatchHunkMatch(target.StartIndex, target.EndIndex, ToPatchMatchKind(kind, usesContextAnchor: true));
    }

    private static PatchHunkMatch LocateNextSequence(
        IReadOnlyList<string> originalLines,
        PatchHunk hunk,
        IReadOnlyList<string> expectedLines,
        int searchStartIndex)
    {
        var match = SeekSequence(originalLines, expectedLines, searchStartIndex, hunk.EndOfFile);
        if (match is not { } located)
        {
            ThrowIfOnlyNonEndOfFileMatchExists(originalLines, hunk, expectedLines, searchStartIndex);
            throw new PatchMatchException("The hunk context does not match the target file at or after the current patch position.");
        }

        EnsureEndOfFile(hunk, located.EndIndex, originalLines.Count);
        return new PatchHunkMatch(
            located.StartIndex,
            located.EndIndex,
            ToPatchMatchKind(located.Kind, usesContextAnchor: false));
    }

    /// <summary>
    /// Searches with Codex-compatible fallback levels, preferring a later strict match over an earlier fuzzy match.
    /// </summary>
    private static SequenceMatch? SeekSequence(
        IReadOnlyList<string> originalLines,
        IReadOnlyList<string> expectedLines,
        int searchStartIndex,
        bool endOfFile)
    {
        if (expectedLines.Count == 0)
        {
            return new SequenceMatch(searchStartIndex, searchStartIndex, PatchTextMatchKind.Exact);
        }

        if (expectedLines.Count > originalLines.Count)
        {
            return null;
        }

        var effectiveSearchStartIndex = endOfFile ? originalLines.Count - expectedLines.Count : searchStartIndex;
        var startIndex = FindFirstMatch(originalLines, expectedLines, effectiveSearchStartIndex, ExactEquals);
        if (startIndex is { } exact)
        {
            return new SequenceMatch(exact, exact + expectedLines.Count, PatchTextMatchKind.Exact);
        }

        startIndex = FindFirstMatch(originalLines, expectedLines, effectiveSearchStartIndex, TrailingWhitespaceEquals);
        if (startIndex is { } trailingWhitespace)
        {
            return new SequenceMatch(
                trailingWhitespace,
                trailingWhitespace + expectedLines.Count,
                PatchTextMatchKind.TrailingWhitespaceFallback);
        }

        startIndex = FindFirstMatch(originalLines, expectedLines, effectiveSearchStartIndex, OuterWhitespaceEquals);
        if (startIndex is { } outerWhitespace)
        {
            return new SequenceMatch(
                outerWhitespace,
                outerWhitespace + expectedLines.Count,
                PatchTextMatchKind.OuterWhitespaceFallback);
        }

        startIndex = FindFirstMatch(originalLines, expectedLines, effectiveSearchStartIndex, UnicodeCompatibilityEquals);
        return startIndex is { } unicodeCompatibility ?
            new SequenceMatch(
                unicodeCompatibility,
                unicodeCompatibility + expectedLines.Count,
                PatchTextMatchKind.UnicodeCompatibilityFallback) :
            null;
    }

    private static int? FindFirstMatch(
        IReadOnlyList<string> originalLines,
        IReadOnlyList<string> expectedLines,
        int searchStartIndex,
        Func<string, string, bool> lineEquals)
    {
        if (expectedLines.Count > originalLines.Count || searchStartIndex > originalLines.Count - expectedLines.Count)
        {
            return null;
        }

        var lastStart = originalLines.Count - expectedLines.Count;
        for (var start = searchStartIndex; start <= lastStart; start++)
        {
            var matchesAtStart = true;
            for (var offset = 0; offset < expectedLines.Count; offset++)
            {
                if (lineEquals(originalLines[start + offset], expectedLines[offset]))
                {
                    continue;
                }

                matchesAtStart = false;
                break;
            }

            if (matchesAtStart)
            {
                return start;
            }
        }

        return null;
    }

    private static bool ExactEquals(string actual, string expected) =>
        string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TrailingWhitespaceEquals(string actual, string expected) =>
        actual.AsSpan().TrimEnd().SequenceEqual(expected.AsSpan().TrimEnd());

    private static bool OuterWhitespaceEquals(string actual, string expected) =>
        actual.AsSpan().Trim().SequenceEqual(expected.AsSpan().Trim());

    private static bool UnicodeCompatibilityEquals(string actual, string expected)
    {
        var actualSpan = actual.AsSpan().Trim();
        var expectedSpan = expected.AsSpan().Trim();
        if (actualSpan.Length != expectedSpan.Length)
        {
            return false;
        }

        for (var index = 0; index < actualSpan.Length; index++)
        {
            if (NormalizeUnicodeCompatibility(actualSpan[index]) != NormalizeUnicodeCompatibility(expectedSpan[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static char NormalizeUnicodeCompatibility(char character) => character switch
    {
        '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2015' or '\u2212' => '-',
        '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
        '\u201C' or '\u201D' or '\u201E' or '\u201F' => '"',
        '\u00A0' or '\u2002' or '\u2003' or '\u2004' or '\u2005' or '\u2006' or '\u2007' or
            '\u2008' or '\u2009' or '\u200A' or '\u202F' or '\u205F' or '\u3000' => ' ',
        _ => character,
    };

    private static PatchMatchKind ToPatchMatchKind(PatchTextMatchKind kind, bool usesContextAnchor) =>
        (kind, usesContextAnchor) switch
        {
            (PatchTextMatchKind.Exact, false) => PatchMatchKind.Exact,
            (PatchTextMatchKind.TrailingWhitespaceFallback, false) => PatchMatchKind.TrailingWhitespaceFallback,
            (PatchTextMatchKind.OuterWhitespaceFallback, false) => PatchMatchKind.OuterWhitespaceFallback,
            (PatchTextMatchKind.UnicodeCompatibilityFallback, false) => PatchMatchKind.UnicodeCompatibilityFallback,
            (PatchTextMatchKind.Exact, true) => PatchMatchKind.Context,
            (PatchTextMatchKind.TrailingWhitespaceFallback, true) => PatchMatchKind.ContextTrailingWhitespaceFallback,
            (PatchTextMatchKind.OuterWhitespaceFallback, true) => PatchMatchKind.ContextOuterWhitespaceFallback,
            (PatchTextMatchKind.UnicodeCompatibilityFallback, true) => PatchMatchKind.ContextUnicodeCompatibilityFallback,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static void ThrowIfOnlyNonEndOfFileMatchExists(
        IReadOnlyList<string> originalLines,
        PatchHunk hunk,
        IReadOnlyList<string> expectedLines,
        int searchStartIndex)
    {
        if (!hunk.EndOfFile)
        {
            return;
        }

        var nonEndOfFileMatch = SeekSequence(originalLines, expectedLines, searchStartIndex, endOfFile: false);
        if (nonEndOfFileMatch is { } located)
        {
            EnsureEndOfFile(hunk, located.EndIndex, originalLines.Count);
        }
    }

    private static void EnsureEndOfFile(PatchHunk hunk, int endIndex, int lineCount)
    {
        if (hunk.EndOfFile && endIndex != lineCount)
        {
            throw new PatchMatchException("The hunk is marked as end-of-file but its context does not end at the end of the file.");
        }
    }

    private enum PatchTextMatchKind
    {
        Exact,
        TrailingWhitespaceFallback,
        OuterWhitespaceFallback,
        UnicodeCompatibilityFallback,
    }

    private readonly record struct SequenceMatch(int StartIndex, int EndIndex, PatchTextMatchKind Kind);
}

/// <summary>
/// Identifies how a hunk was matched.
/// </summary>
public enum PatchMatchKind
{
    Exact,
    TrailingWhitespaceFallback,
    OuterWhitespaceFallback,
    UnicodeCompatibilityFallback,
    Context,
    ContextTrailingWhitespaceFallback,
    ContextOuterWhitespaceFallback,
    ContextUnicodeCompatibilityFallback,
}

/// <summary>
/// Describes a located hunk as a zero-based half-open logical-line range.
/// </summary>
public sealed record PatchHunkMatch(int StartIndex, int EndIndex, PatchMatchKind Kind);

/// <summary>
/// Reports a hunk location failure that must prevent any filesystem mutation.
/// </summary>
public sealed class PatchMatchException(string message, Exception? innerException = null) : InvalidOperationException(message, innerException);