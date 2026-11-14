using Avalonia.Controls;
using Everywhere.Chat.Links;

namespace Everywhere.Interop;

/// <summary>
/// Desktop file actions invoked only by explicit chat link commands.
/// Acceptance means the system accepted the request, not that another app finished opening it.
/// </summary>
public interface ILinkFileOperations
{
    /// <summary>
    /// Opens a directory in the file manager without launching application bundles.
    /// </summary>
    Task<ChatLinkOperationStatus> OpenDirectoryAsync(string path, TopLevel owner, CancellationToken cancellationToken);

    /// <summary>
    /// Selects a file or directory in its parent, or opens the parent if selection is unavailable.
    /// </summary>
    Task<ChatLinkOperationStatus> RevealItemAsync(string path, bool isDirectory, TopLevel owner, CancellationToken cancellationToken);

    /// <summary>
    /// Lets the user choose an app for a single open without changing the default association.
    /// </summary>
    Task<ChatLinkOperationStatus> OpenFileWithAsync(string path, TopLevel owner, CancellationToken cancellationToken);
}