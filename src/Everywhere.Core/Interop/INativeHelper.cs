using System.Runtime.Versioning;
using Avalonia.Input;

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
    bool IsUserStartupEnabled { get; set; }

    /// <summary>
    /// Check if the application is set to start with the system as Administrator (aka UAC on Windows).
    /// This can only be set if the current user is an administrator.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Thrown if the current user is not an administrator.</exception>
    [SupportedOSPlatform("windows")]
    bool IsAdministratorStartupEnabled { get; set; }

    /// <summary>
    /// Get whether the low data mode is currently active.
    /// This can be used to reduce data usage when the user is on a metered network connection.
    /// e.g. automatically download updates.
    /// </summary>
    bool IsLowDataModeActive { get; }

    /// <summary>
    /// Restart the application as administrator (aka UAC on Windows).
    /// </summary>
    [SupportedOSPlatform("windows")]
    void RestartAsAdministrator();

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
}