using System.Text.RegularExpressions;
using Everywhere.Common;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Extracts logical text lines and search matches from local PDF documents.
/// </summary>
public sealed class PdfFileHandler : LocalFileHandler
{
    protected override async ValueTask<bool> CanHandleLocalAsync(string path, FileSystemInfo fileSystemInfo, CancellationToken cancellationToken)
    {
        if (fileSystemInfo is not FileInfo { Exists: true }) return false;
        if (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return true;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var header = new byte[5];
        return await stream.ReadAsync(header, cancellationToken) == header.Length &&
            header.AsSpan().SequenceEqual("%PDF-"u8);
    }

    public override ValueTask<FileReadResult> ReadAsync(
        FileHandlerContext context,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(ReadCore(context, offset, limit, cancellationToken));

    public override ValueTask<FileContentSearchResult> SearchContentAsync(
        FileHandlerContext context,
        Regex searchPattern,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(SearchCore(context, searchPattern, cancellationToken));

    private static FileReadResult ReadCore(FileHandlerContext context, int offset, int limit, CancellationToken cancellationToken)
    {
        var file = EnsureFile(context);
        if (file.Length > 100L * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException("PDF size is larger than 100 MB, read operation is not supported."),
                LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage);
        }

        using var document = OpenDocument(file.FullName);
        var startLine = Math.Max(1, offset);
        var actualLimit = Math.Clamp(limit, 1, 2000);
        var items = new List<FileReadResult.Item>(actualLimit);
        var currentLine = 0;
        var hasMore = false;
        var hasText = false;
        int? firstPage = null;
        int? lastPage = null;

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ContentOrderTextExtractor.GetText(page);
            hasText |= !string.IsNullOrWhiteSpace(text);
            using var reader = new StringReader(text);
            while (reader.ReadLine() is { } line)
            {
                currentLine++;
                if (currentLine < startLine) continue;
                if (items.Count >= actualLimit)
                {
                    hasMore = true;
                    break;
                }

                items.Add(new FileReadResult.Item(line, PageNumber: page.Number));
                firstPage ??= page.Number;
                lastPage = page.Number;
            }

            if (hasMore) break;
        }

        if (!hasMore && !hasText)
        {
            throw new HandledException(
                new InvalidDataException("The PDF contains no extractable text. It may be a scanned document; OCR is not supported."),
                LocaleKey.BuiltInChatPlugin_FileSystem_PdfNoText_ErrorMessage);
        }

        var metadata = new Dictionary<string, object?>
        {
            ["totalPages"] = document.NumberOfPages,
            ["firstPage"] = firstPage,
            ["lastPage"] = lastPage
        };
        return new FileReadResult
        {
            Items = items,
            Offset = startLine,
            Unit = "line",
            Total = hasMore ? null : currentLine,
            HasMore = hasMore,
            Metadata = metadata
        };
    }

    private static FileContentSearchResult SearchCore(FileHandlerContext context, Regex searchPattern, CancellationToken cancellationToken)
    {
        const int maxMatches = 2000;
        var file = EnsureFile(context);
        if (file.Length > 10L * 1024 * 1024) return new FileContentSearchResult([], LimitHit: true);

        using var document = OpenDocument(file.FullName);
        var results = new List<FileContentMatch>();
        var linesBeforePage = 0;
        var hasText = false;
        var limitHit = false;

        foreach (var page in document.GetPages())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var content = ContentOrderTextExtractor.GetText(page);
            hasText |= !string.IsNullOrWhiteSpace(content);
            var pageResult = FindMatches(
                file.FullName,
                content,
                searchPattern,
                cancellationToken,
                linesBeforePage,
                page.Number,
                maxMatches - results.Count);
            results.AddRange(pageResult.Matches);
            if (pageResult.LimitHit || results.Count >= maxMatches)
            {
                limitHit = true;
                break;
            }

            // PDF offsets are global logical lines even though extraction happens page by page.
            using var reader = new StringReader(content);
            while (reader.ReadLine() is not null) linesBeforePage++;
        }

        if (!hasText)
        {
            throw new HandledException(
                new InvalidDataException("The PDF contains no extractable text. It may be a scanned document; OCR is not supported."),
                LocaleKey.BuiltInChatPlugin_FileSystem_PdfNoText_ErrorMessage);
        }

        return new FileContentSearchResult(results, limitHit);
    }

    private static PdfDocument OpenDocument(string path)
    {
        try
        {
            return PdfDocument.Open(path);
        }
        catch (PdfDocumentEncryptedException ex)
        {
            throw new HandledException(
                new InvalidDataException("The PDF is encrypted. Password-protected PDF files are not supported.", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_PdfEncrypted_ErrorMessage);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new HandledException(
                new InvalidDataException("The PDF is damaged or has an unsupported structure.", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_PdfDamaged_ErrorMessage);
        }
    }
}