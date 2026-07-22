using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Provides hexadecimal reads and directory fallback handling for local resources.
/// </summary>
public sealed class BinaryFileHandler : LocalFileHandler
{
    protected override ValueTask<bool> CanHandleLocalAsync(string path, FileSystemInfo fileSystemInfo, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(true);
    }

    public override async ValueTask<FileReadResult> ReadAsync(
        FileHandlerContext context,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        var file = EnsureFile(context);
        if (file.Length > 100L * 1024 * 1024)
        {
            throw new HandledException(
                new NotSupportedException(
                    $"The file '{file.FullName}' is larger than 100 MB, so the read operation is not supported."),
                new FormattedDynamicLocaleKey(
                    LocaleKey.BuiltInChatPlugin_FileSystem_ReadFile_FileTooLarge_ErrorMessage,
                    100));
        }

        var startByte = ToZeroBasedOffset(offset);
        var maxBytes = Math.Clamp(limit == 2000 ? 10240 : limit, 1, 1024 * 1024);
        await using var stream = Open(context, FileMode.Open, FileAccess.Read, FileShare.Read);
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
            Total = file.Length <= int.MaxValue ? (int)file.Length : null,
            HasMore = stream.Position < stream.Length
        };
    }
}