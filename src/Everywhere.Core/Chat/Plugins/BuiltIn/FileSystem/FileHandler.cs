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
    internal abstract ValueTask<FileHandlerContext?> TryCreateContextAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken);

    public virtual ValueTask<FileRecord> GetFileInformationAsync(
        FileHandlerContext context,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<FileRecord>(UnsupportedOperation(context, "get file information"));

    public virtual IAsyncEnumerable<string> EnumerateAsync(
        FileHandlerContext context,
        Regex filePattern,
        bool recurseSubdirectories,
        bool ignoreCommonBuildFolders,
        CancellationToken cancellationToken) =>
        UnsupportedEnumerable(context, "enumerate files", cancellationToken);

    public virtual ValueTask<FileReadResult> ReadAsync(
        FileHandlerContext context,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<FileReadResult>(UnsupportedOperation(context, "read"));

    public virtual ValueTask<FileContentSearchResult> SearchContentAsync(
        FileHandlerContext context,
        Regex searchPattern,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<FileContentSearchResult>(UnsupportedOperation(context, "search content"));

    public virtual ValueTask WriteAsync(
        FileHandlerContext context,
        string? content,
        bool append,
        CancellationToken cancellationToken) =>
        ValueTask.FromException(UnsupportedOperation(context, append ? "append" : "write"));

    public virtual ValueTask<string> ReplaceContentAsync(
        FileHandlerContext context,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> replacements,
        bool isRegex,
        bool ignoreCase,
        Func<string, string, CancellationToken, Task<FileReviewResult>> reviewAsync,
        CancellationToken cancellationToken) =>
        ValueTask.FromException<string>(UnsupportedOperation(context, "replace content"));

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
        foreach (Match match in searchPattern.Matches(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (matches.Count >= maxMatches) return new FileContentSearchResult(matches, LimitHit: true);

            var lineStart = content.LastIndexOf('\n', Math.Max(0, match.Index - 1)) + 1;
            var lineEnd = content.IndexOf('\n', match.Index + match.Length);
            if (lineEnd < 0) lineEnd = content.Length;
            if (lineEnd > lineStart && content[lineEnd - 1] == '\r') lineEnd--;

            matches.Add(
                new FileContentMatch(
                    path,
                    content[lineStart..lineEnd],
                    lineOffset + content.AsSpan(0, match.Index).Count('\n') + 1,
                    match.Index - lineStart + 1,
                    match.Length,
                    pageNumber));
        }

        return new FileContentSearchResult(matches);
    }

    private HandledException UnsupportedOperation(FileHandlerContext context, string operation) => new(
        new NotSupportedException($"The handler '{GetType().Name}' does not support the '{operation}' operation for '{context.Path}'."),
        LocaleKey.BuiltInChatPlugin_FileSystem_UnsupportedOperation_ErrorMessage);

    private IAsyncEnumerable<string> UnsupportedEnumerable(FileHandlerContext context, string operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw UnsupportedOperation(context, operation);
    }
}