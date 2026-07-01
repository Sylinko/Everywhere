using System.Diagnostics;
using Avalonia.Input;
using Everywhere.Extensions;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;

public class NativeHelper(IEventHelper eventHelper) : INativeHelper
{
    private static string AutostartFolder => 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/autostart");

    private static string ShortcutFile => Path.Combine(AutostartFolder, "Everywhere.desktop");

    /// <summary>
    /// Gets the proper executable path, handling AppImage environments.
    /// </summary>
    private static string GetExecutablePath()
    {
        // If running as AppImage, use the environment variable provided by the AppImage runtime
        string? appImagePath = Environment.GetEnvironmentVariable("APPIMAGE");
        if (!string.IsNullOrEmpty(appImagePath))
        {
            return appImagePath;
        }

        // Fallback to current process path
        return Process.GetCurrentProcess().MainModule?.FileName ?? "/usr/bin/Everywhere";
    }

    public bool IsInstalled => File.Exists("/usr/bin/Everywhere");

    public bool IsAdministrator => Environment.UserName == "root";

    public bool IsUserStartupEnabled
    {
        get => File.Exists(ShortcutFile);
        set
        {
            if (value)
            {
                string execPath = GetExecutablePath();
                string content =
                    $"""
                    [Desktop Entry]
                    Type=Application
                    Name=Everywhere
                    Comment=Everywhere Startup Service
                    Exec="{execPath}"
                    Icon=Everywhere
                    Terminal=false
                    Categories=Utility;
                    X-GNOME-Autostart-enabled=true
                    """;

                if (!Directory.Exists(AutostartFolder))
                    Directory.CreateDirectory(AutostartFolder);

                File.WriteAllText(ShortcutFile, content);
            }
            else if (File.Exists(ShortcutFile))
            {
                File.Delete(ShortcutFile);
            }
        }
    }

    public bool IsAdministratorStartupEnabled { get; set; }

    public bool IsLowDataModeActive => throw new NotImplementedException();

    public void RestartAsAdministrator()
    {
        throw new NotSupportedException();
    }

    public bool GetKeyState(KeyModifiers keyModifiers)
    {
        return eventHelper.GetKeyState(keyModifiers);
    }

    public Task<bool> ShowDesktopNotificationAsync(string message, string? title = null)
    {
        // Try to use libnotify via command line as a best-effort notification
        var args = $"-u normal \"{title ?? "Everywhere"}\" \"{message}\"";
        Process.Start("notify-send", args);
        return Task.FromResult(false);
    }

    public void OpenFileLocation(string fullPath)
    {
        if (fullPath.IsNullOrWhiteSpace()) return;
        var args = $"\"{fullPath}\"";
        Process.Start(new ProcessStartInfo("xdg-open", args) { UseShellExecute = true });
    }
}