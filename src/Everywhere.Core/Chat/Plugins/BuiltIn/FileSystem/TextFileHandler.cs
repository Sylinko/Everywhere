using System.Text;
using System.Text.RegularExpressions;
using Everywhere.Common;
using Everywhere.Utilities;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Handles local text files, including encoding-aware reads, writes, searches, and replacements.
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
                new NotSupportedException("File size is larger than 100 MB, read operation is not supported."),
                LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage);
        }

        await using var stream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read);
        var encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) ??
            throw new HandledException(
                new InvalidDataException("The file is not recognized as text."),
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
        if (file.Length > 10L * 1024 * 1024) return new FileContentSearchResult([]);

        await using var stream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read);
        var encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken);
        if (encoding is null) return new FileContentSearchResult([]);
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return FindMatches(file.FullName, content, searchPattern, cancellationToken);
    }

    public override async ValueTask WriteAsync(FileHandlerContext context, string? content, bool append, CancellationToken cancellationToken)
    {
        var existed = File.Exists(context.Path);
        Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        if (existed)
        {
            await using var readStream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read);
            encoding = await EncodingDetector.DetectEncodingAsync(readStream, cancellationToken: cancellationToken) ??
                throw new HandledException(
                    new InvalidDataException("Cannot write to a binary file."),
                    LocaleKey.BuiltInChatPlugin_FileSystem_WriteToFile_BinaryFile_Write_ErrorMessage);
        }

        await using var stream = Open(
            context,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await using var writer = new StreamWriter(stream, encoding);
        await writer.WriteAsync(content);
        await writer.FlushAsync(cancellationToken);
        context.FileSystemInfo?.Refresh();
    }

    public override async ValueTask<string> ReplaceContentAsync(
        FileHandlerContext context,
        IReadOnlyList<string> patterns,
        IReadOnlyList<string> replacements,
        bool isRegex,
        bool ignoreCase,
        Func<string, string, CancellationToken, Task<FileReviewResult>> reviewAsync,
        CancellationToken cancellationToken)
    {
        var file = EnsureFile(context);
        if (file.Length > 10L * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException("File size is larger than 10 MB, replace operation is not supported."),
                LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_FileTooLarge_ErrorMessage);
        }

        string originalContent;
        Encoding encoding;
        await using (var stream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken) ??
                throw new HandledException(
                    new InvalidDataException("Cannot replace content in a binary file."),
                    LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_BinaryFile_ErrorMessage);
            stream.Seek(0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
            originalContent = await reader.ReadToEndAsync(cancellationToken);
        }

        var options = RegexOptions.Compiled | RegexOptions.Multiline;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        var replacedContent = originalContent;
        for (var i = 0; i < patterns.Count; i++)
        {
            try
            {
                replacedContent = isRegex ?
                    new Regex(patterns[i], options, TimeSpan.FromSeconds(3)).Replace(replacedContent, replacements[i]) :
                    replacedContent.Replace(
                        patterns[i],
                        replacements[i],
                        ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (ArgumentException ex)
            {
                throw new HandledException(ex, LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPattern_ErrorMessage);
            }
        }

        if (replacedContent == originalContent) return "No content was replaced.";
        // The read stream is deliberately closed before review so the UI may wait indefinitely
        // without locking the file. Acceptance is followed by an exclusive conflict check.
        var review = await reviewAsync(originalContent, replacedContent, cancellationToken);
        if (review.AcceptedContent is null) return review.Summary;

        await using var writeStream = Open(context, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using (var reader = new StreamReader(writeStream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true))
        {
            var currentContent = await reader.ReadToEndAsync(cancellationToken);
            if (!currentContent.Equals(originalContent, StringComparison.Ordinal))
            {
                throw new HandledException(
                    new IOException("The file changed while the replacement was awaiting approval. No changes were written."),
                    LocaleKey.BuiltInChatPlugin_FileSystem_ReplaceFileContent_Conflict_ErrorMessage);
            }
        }

        writeStream.SetLength(0);
        writeStream.Seek(0, SeekOrigin.Begin);
        await using var writer = new StreamWriter(writeStream, encoding, bufferSize: 1024, leaveOpen: true);
        await writer.WriteAsync(review.AcceptedContent);
        await writer.FlushAsync(cancellationToken);
        return review.Summary;
    }
}