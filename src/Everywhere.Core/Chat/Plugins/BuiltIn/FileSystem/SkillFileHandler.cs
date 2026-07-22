using System.Text.RegularExpressions;
using Everywhere.Common;
using Everywhere.Skills;
using Everywhere.Utilities;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Exposes physical and virtual skill resources through canonical <c>skill://</c> URIs.
/// </summary>
public sealed class SkillFileHandler(SkillManager skillManager) : FileHandler
{
    public override ValueTask<FileHandlerContext?> TryCreateContextAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("skill", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult<FileHandlerContext?>(null);
        }

        try
        {
            var resource = skillManager.ResolveResource(path);
            return ValueTask.FromResult<FileHandlerContext?>(new FileHandlerContext(this, resource.Uri, workingDirectory));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or UriFormatException)
        {
            throw new HandledException(
                new InvalidOperationException($"The skill resource '{path}' is unavailable: {ex.Message}", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_SkillResourceUnavailable_ErrorMessage);
        }
    }

    public override ValueTask<FileRecord> GetFileInformationAsync(
        FileHandlerContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resource = Resolve(context);
        return ValueTask.FromResult(
            new FileRecord(
                resource.Uri,
                resource.IsDirectory ? -1 : resource.Length,
                null,
                null,
                resource.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal));
    }

    // ReSharper disable once AsyncMethodWithoutAwait
    public override async IAsyncEnumerable<string> EnumerateAsync(
        FileHandlerContext context,
        Regex filePattern,
        bool recurseSubdirectories,
        bool ignoreCommonBuildFolders,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var resource in skillManager.EnumerateResources(context.Path, recurseSubdirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = resource.RelativePath.Length == 0 ? new Uri(resource.Uri).Host : resource.RelativePath.Split('/').Last();
            if (filePattern.IsMatch(name)) yield return resource.Uri;
        }
    }

    public override async ValueTask<FileReadResult> ReadAsync(
        FileHandlerContext context,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var resource = Resolve(context);
        if (resource.IsDirectory)
        {
            throw new HandledException(
                new InvalidOperationException(
                    $"The requested skill resource is a directory, but this operation requires a file: '{context.Path}'."),
                LocaleKey.BuiltInChatPlugin_FileSystem_EnsureFileInfo_PathIsDirectory_ErrorMessage);
        }
        if (resource.Length > 100L * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The skill resource '{resource.Uri}' is larger than 100 MB, so the read operation is not supported."),
                new FormattedDynamicLocaleKey(
                    LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage,
                    100));
        }

        await using var stream = resource.OpenRead();
        var encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken);
        if (encoding is null)
        {
            stream.Seek(0, SeekOrigin.Begin);
            return await ReadBinaryAsync(resource, stream, offset, limit, cancellationToken);
        }

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

        return new FileReadResult
        {
            Items = items,
            Offset = startLine,
            Unit = "line",
            Total = hasMore ? null : currentLine,
            HasMore = hasMore
        };
    }

    public override async ValueTask<FileContentSearchResult> SearchContentAsync(
        FileHandlerContext context,
        Regex searchPattern,
        CancellationToken cancellationToken)
    {
        var resource = Resolve(context);
        if (resource.IsDirectory || resource.Length > 10L * 1024 * 1024)
        {
            return new FileContentSearchResult([], LimitHit: resource.Length > 10L * 1024 * 1024);
        }

        await using var stream = resource.OpenRead();
        var encoding = await EncodingDetector.DetectEncodingAsync(stream, cancellationToken: cancellationToken);
        if (encoding is null) return new FileContentSearchResult([]);
        stream.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        var content = await reader.ReadToEndAsync(cancellationToken);
        return FindMatches(resource.Uri, content, searchPattern, cancellationToken);
    }

    private SkillResource Resolve(FileHandlerContext context)
    {
        try
        {
            return skillManager.ResolveResource(context.Path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException or UriFormatException)
        {
            throw new HandledException(
                new InvalidOperationException($"The skill resource '{context.Path}' is unavailable: {ex.Message}", ex),
                LocaleKey.BuiltInChatPlugin_FileSystem_SkillResourceUnavailable_ErrorMessage);
        }
    }

    private static async ValueTask<FileReadResult> ReadBinaryAsync(
        SkillResource resource,
        Stream stream,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var startByte = ToZeroBasedOffset(offset);
        var maxBytes = Math.Clamp(limit == 2000 ? 10240 : limit, 1, 1024 * 1024);
        stream.Seek(startByte, SeekOrigin.Begin);
        var items = new List<FileReadResult.Item>();
        var buffer = new byte[Math.Min(32, maxBytes)];
        var remaining = maxBytes;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) break;
            items.Add(new FileReadResult.Item(BitConverter.ToString(buffer, 0, read), read));
            remaining -= read;
        }

        return new FileReadResult
        {
            Items = items,
            Offset = ToOneBasedOffset(startByte),
            Unit = "byte",
            Total = resource.Length <= int.MaxValue ? (int)resource.Length : null,
            HasMore = stream.Position < stream.Length
        };
    }
}