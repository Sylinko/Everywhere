using System.Runtime.Versioning;
using Avalonia.Input;
using Avalonia.Media.Imaging;

namespace Everywhere.Interop;

public interface INativeHelper
{
    /// <summary>
    /// Check if the application is installed in the system.
    /// </summary>
    bool IsInstalled { get; }

    /// <summary>
    /// Check if the current user is an administrator (aka UAC on Windows).
    /// </summary>
    [SupportedOSPlatform("windows")]
    bool IsAdministrator { get; }

    /// <summary>
    /// Check if the application is set to start with the system as User.
    /// </summary>
    bool IsStartupEnabled { get; set; }

    /// <summary>
    /// Get whether the low data mode is currently active.
    /// This can be used to reduce data usage when the user is on a metered network connection.
    /// e.g. automatically download updates.
    /// </summary>
    bool IsLowDataModeActive { get; }

    /// <summary>
    /// Get the current state of the given key. True if the key is currently pressed down, false otherwise.
    /// </summary>
    /// <param name="keyModifiers"></param>
    /// <returns></returns>
    bool GetKeyState(KeyModifiers keyModifiers);

    /// <summary>
    /// Show a desktop notification with the given message and optional title. returns true if the notification was clicked or confirmed by the user.
    /// </summary>
    /// <param name="message"></param>
    /// <param name="title"></param>
    Task<bool> ShowDesktopNotificationAsync(string message, string? title = null);

    /// <summary>
    /// Open the file location in the system file explorer and select the file.
    /// </summary>
    /// <param name="fullPath"></param>
    void OpenFileLocation(string fullPath);

    /// <summary>
    /// Moves the supplied files or directories to the platform trash. Never falls back to permanent deletion.
    /// Cancellation stops further work and requests interruption where supported, without rolling back
    /// completed items. The task completes only after native work stops; cancellation arriving after
    /// all items have completed does not turn success into cancellation.
    /// </summary>
    /// <exception cref="OperationCanceledException">The token or native progress UI canceled the operation.</exception>
    /// <exception cref="IOException">An item could not be moved to the trash; earlier items may have completed.</exception>
    Task MoveToTrashAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an absolute application path or saved native application identity without launching it.
    /// Returns null when the application cannot be resolved.
    /// </summary>
    NativeApplicationInfo? GetApplicationInfo(string identityOrPath);

    /// <summary>
    /// Resolves the application owning a selected window's process. Does not fall back to a process-name rule.
    /// </summary>
    NativeApplicationInfo? GetApplicationInfo(int processId);

    /// <summary>
    /// Loads a platform application icon, or the platform default when the path is null or unavailable.
    /// The caller owns and must dispose the returned bitmap.
    /// </summary>
    Bitmap? GetApplicationIcon(string? path);
}