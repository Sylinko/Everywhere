using Everywhere.Common;

namespace Everywhere.Chat.Plugins.BuiltIn.FileSystem;

/// <summary>
/// Selects the first registered handler that can fully resolve a file-like resource.
/// </summary>
public sealed class FileHandlerContextFactory(IEnumerable<FileHandler> handlers)
{
    private readonly IReadOnlyList<FileHandler> _handlers = [.. handlers];

    public async ValueTask<FileHandlerContext> CreateAsync(
        string path,
        string workingDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new HandledException(
                new ArgumentException("Path cannot be null or empty.", nameof(path)),
                LocaleKey.BuiltInChatPlugin_FileSystem_InvalidPath_ErrorMessage);
        }

        foreach (var handler in _handlers)
        {
            if (await handler.TryCreateContextAsync(path, workingDirectory, cancellationToken) is { } context)
            {
                return context;
            }
        }

        throw new HandledException(
            new NotSupportedException(
                $"No available file handler supports the resource '{path}'. The path may use an unsupported URI scheme or file format."),
            LocaleKey.BuiltInChatPlugin_FileSystem_UnsupportedResource_ErrorMessage);
    }
}