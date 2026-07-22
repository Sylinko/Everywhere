using System.Text.RegularExpressions;
using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Resolves a file-like resource and exposes the operations supported by its format.
/// </summary>
/// <remarks>
/// Registration order forms the handler chain. A handler returns a complete context when it
/// claims a resource, so callers never observe a partially initialized selection.
/// </remarks>
public abstract class FileHandler
{
    /// <summary>
    /// Converts a public one-based offset to a zero-based index, clamping values at or below one to zero.
    /// </summary>
    protected static int ToZeroBasedOffset(int oneBasedOffset) => oneBasedOffset <= 1 ? 0 : oneBasedOffset - 1;

    /// <summary>
    /// Converts a zero-based index to a public one-based offset without overflowing.
    /// </summary>
    protected static int ToOneBasedOffset(int zeroBasedOffset) =>
        zeroBasedOffset >= int.MaxValue ? int.MaxValue : zeroBasedOffset + 1;

    /// <summary>
    /// Attempts to claim a resource and create its fully resolved operation context.
    /// </summary>
    /// <returns>
    /// A context associated with this handler, or <see langword="null"/> when the next registered handler should be tried.
    /// </returns>
    public abstract ValueTask<FileHandlerContext?> TryCreateContextAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets metadata for the resource represented by <paramref name="context"/>.
    /// </summary>
    /// <remarks>The default implementation reports that the operation is unsupported.</remarks>
    public virtual ValueTask<FileRecord> GetFileInformationAsync(
        FileHandlerContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<FileRecord>(UnsupportedOperation(context, "get file information"));

    /// <summary>
    /// Enumerates matching entries beneath a resource.
    /// </summary>
    /// <remarks>The default implementation reports that the operation is unsupported.</remarks>
    public virtual IAsyncEnumerable<string> EnumerateAsync(
        FileHandlerContext context,
        Regex filePattern,
        bool recurseSubdirectories,
        bool ignoreCommonBuildFolders,
        CancellationToken cancellationToken) =>
        UnsupportedEnumerable(context, "enumerate files", cancellationToken);

    /// <summary>
    /// Reads a bounded range of one-based logical units from a resource.
    /// </summary>
    /// <remarks>
    /// A logical unit is format-specific, such as a text line, PDF line, or binary block.
    /// The default implementation reports that the operation is unsupported.
    /// </remarks>
    public virtual ValueTask<FileReadResult> ReadAsync(
        FileHandlerContext context,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<FileReadResult>(UnsupportedOperation(context, "read"));

    /// <summary>
    /// Searches a resource and returns structured occurrences with their logical locations.
    /// </summary>
    /// <remarks>The default implementation reports that the operation is unsupported.</remarks>
    public virtual ValueTask<FileContentSearchResult> SearchContentAsync(
        FileHandlerContext context,
        Regex searchPattern,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<FileContentSearchResult>(UnsupportedOperation(context, "search content"));

    /// <summary>
    /// Writes or appends content to a resource.
    /// </summary>
    /// <remarks>The default implementation reports that the operation is unsupported.</remarks>
    public virtual ValueTask WriteAsync(
        FileHandlerContext context,
        string? content,
        bool append,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(UnsupportedOperation(context, append ? "append" : "write"));

    /// <summary>
    /// Produces replacement content, requests review, and applies the accepted result.
    /// </summary>
    /// <remarks>The default implementation reports that the operation is unsupported.</remarks>
    public virtual ValueTask<string> ReplaceContentAsync(
        FileHandlerContext context,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> replacements,
        bool isRegex,
        bool ignoreCase,
        Func<string, string, CancellationToken, Task<FileReviewResult>> reviewAsync,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<string>(UnsupportedOperation(context, "replace content"));

    /// <summary>
    /// Converts regex matches in a text segment into structured, line-oriented search results.
    /// </summary>
    /// <remarks>
    /// <paramref name="lineOffset"/> shifts reported line numbers for segmented content, while
    /// <paramref name="pageNumber"/> associates every match with an optional format-specific page.
    /// </remarks>
    protected static FileContentSearchResult FindMatches(
        string path,
        string content,
        Regex searchPattern,
        CancellationToken cancellationToken,
        int lineOffset = 0,
        int? pageNumber = null,
        int maxMatches = 2000)
    {
        var matches = new List<FileContentMatch>();
        var lineStart = 0;
        var lineNumber = lineOffset + 1;
        var nextLineBreak = content.IndexOf('\n');
        foreach (Match match in searchPattern.Matches(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxMatches) return new FileContentSearchResult(matches, LimitHit: true);

            while (nextLineBreak >= 0 && nextLineBreak < match.Index)
            {
                lineStart = nextLineBreak + 1;
                lineNumber++;
                nextLineBreak = content.IndexOf('\n', lineStart);
            }

            var lineEnd = content.IndexOf('\n', match.Index + match.Length);
            if (lineEnd < 0) lineEnd = content.Length;
            if (lineEnd > lineStart && content[lineEnd - 1] == '\r') lineEnd--;

            matches.Add(
                new FileContentMatch(
                    path,
                    content[lineStart..lineEnd],
                    lineNumber,
                    match.Index - lineStart + 1,
                    match.Length,
                    pageNumber));
        }

        return new FileContentSearchResult(matches);
    }

    private HandledException UnsupportedOperation(FileHandlerContext context, string operation) => new(
        new NotSupportedException(
            $"The file handler '{GetType().Name}' cannot perform the '{operation}' operation for '{context.Path}'. The resource format is not supported for that operation."),
        LocaleKey.BuiltInChatPlugin_FileSystem_UnsupportedOperation_ErrorMessage);

    private IAsyncEnumerable<string> UnsupportedEnumerable(FileHandlerContext context, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw UnsupportedOperation(context, operation);
    }
}