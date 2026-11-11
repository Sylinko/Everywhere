using System.Text.RegularExpressions;
using Everywhere.Common;
using Everywhere.Utilities;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Handles local text files, including encoding-aware reads and searches.
/// </summary>
public sealed class TextFileHandler : LocalFileHandler
{
    protected override async ValueTask<bool> CanHandleLocalAsync(string path, FileSystemInfo fileSystemInfo, CancellationToken cancellationToken)
    {
        if (fileSystemInfo is DirectoryInfo) return false;
        if (fileSystemInfo is FileInfo { Exists: false }) return true;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) is not null;
    }

    public override async ValueTask<FileReadResult> ReadAsync(FileHandlerContext context, int offset, int limit, CancellationToken cancellationToken)
    {
        var file = EnsureFile(context);
        if (file.Length > 100L * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The file '{file.FullName}' is larger than 100 MiB, so the read operation is not supported."),
                new FormattedDynamicLocaleKey(
                    LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage,
                    100));
        }

        await using var stream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read);
        var encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) ??
            throw new HandledException(
                new InvalidDataException(
                    $"The file '{context.Path}' is not recognized as a text file, so it cannot be read as text."),
                LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_BinaryFile_ErrorMessage);
        stream.Seek(0, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var startLine = Math.Max(1, offset);
        var actualLimit = Math.Clamp(limit, 1, 2000);
        var currentLine = 0;
        var items = new List<FileReadResult.Item>(actualLimit);
        var hasMore = false;

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            currentLine++;
            if (currentLine < startLine) continue;
            if (items.Count >= actualLimit)
            {
                hasMore = true;
                break;
            }

            items.Add(new FileReadResult.Item(line));
        }

        int? totalLines = null;
        if (!hasMore)
        {
            totalLines = currentLine;
        }
        else if (file.Length < 1024 * 1024)
        {
            totalLines = currentLine;
            while (await reader.ReadLineAsync(cancellationToken) is not null) totalLines++;
        }

        return new FileReadResult
        {
            Items = items,
            Offset = startLine,
            Unit = "line",
            Total = totalLines,
            HasMore = hasMore
        };
    }

    public override async ValueTask<FileContentSearchResult> SearchContentAsync(
        FileHandlerContext context,
        Regex searchPattern,
        CancellationToken cancellationToken)
    {
        var file = EnsureFile(context);
        if (file.Length > 10L * 1024 * 1024) return new FileContentSearchResult([], LimitHit: true);

        await using var stream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read);
        var encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken);
        if (encoding is null) return new FileContentSearchResult([]);
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return FindMatches(file.FullName, content, searchPattern, cancellationToken);
    }
}