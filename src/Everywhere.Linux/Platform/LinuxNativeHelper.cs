using System.Diagnostics;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Everywhere.Interop;

namespace Everywhere.Linux.Platform;

/// <summary>
/// Linux application metadata and desktop integration; resolving an identity never launches it.
/// </summary>
public sealed class LinuxNativeHelper : INativeHelper
{
    private static string StartupPath
    {
        get => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config/autostart/Everywhere.desktop");
    }

    /// <inheritdoc />
    public bool IsInstalled => File.Exists("/usr/bin/Everywhere");

    /// <inheritdoc />
    public bool IsAdministrator => Environment.UserName == "root";

    /// <inheritdoc />
    public bool IsLowDataModeActive => false;

    /// <inheritdoc />
    public bool IsStartupEnabled
    {
        get => File.Exists(StartupPath);
        set
        {
            if (!value)
            {
                File.Delete(StartupPath);
                return;
            }

            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Application path is unavailable.");
            Directory.CreateDirectory(Path.GetDirectoryName(StartupPath) ?? throw new InvalidOperationException());
            File.WriteAllText(
                StartupPath,
                $"[Desktop Entry]\nType=Application\nName=Everywhere\nExec=\"{executable.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"\nIcon=Everywhere\n");
        }
    }

    /// <inheritdoc />
    public NativeApplicationInfo? GetApplicationInfo(int processId)
    {
        try
        {
            var path = File.ResolveLinkTarget($"/proc/{processId}/exe", true)?.FullName;
            return path is null ? null : GetApplicationInfo(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <inheritdoc />
    public NativeApplicationInfo? GetApplicationInfo(string identityOrPath)
    {
        if (!Path.IsPathFullyQualified(identityOrPath) || !File.Exists(identityOrPath))
        {
            return null;
        }

        var path = Path.GetFullPath(identityOrPath);
        if (path.EndsWith(".desktop", StringComparison.Ordinal))
        {
            var entries = File.ReadLines(path).Where(line => !line.StartsWith('#') && line.Contains('='))
                .Select(line => line.Split('=', 2)).GroupBy(parts => parts[0]).ToDictionary(group => group.Key, group => group.First()[1]);
            if (!entries.TryGetValue("Exec", out var command))
            {
                return null;
            }

            var executable = command.StartsWith('"') ? command.Split('"').ElementAtOrDefault(1) : command.Split(' ', 2)[0];
            if (string.IsNullOrEmpty(executable))
            {
                return null;
            }

            if (!Path.IsPathFullyQualified(executable))
            {
                executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
                    .Select(directory => Path.Combine(directory, executable)).FirstOrDefault(File.Exists);
            }

            if (executable is null || !File.Exists(executable))
            {
                return null;
            }

            return new NativeApplicationInfo(Path.GetFullPath(executable), entries.GetValueOrDefault("Name", Path.GetFileName(executable)), path);
        }

        return new NativeApplicationInfo(path, Path.GetFileName(path), path);
    }

    /// <inheritdoc />
    public Bitmap? GetApplicationIcon(string? path) => null;

    /// <inheritdoc />
    public bool GetKeyState(KeyModifiers keyModifiers) => false;

    /// <inheritdoc />
    public async Task<bool> ShowDesktopNotificationAsync(string message, string? title = null)
    {
        var start = new ProcessStartInfo("notify-send")
        {
            UseShellExecute = false
        };
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(title ?? "Everywhere");
        start.ArgumentList.Add(message);
        using var process = Process.Start(start);
        if (process is not null)
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }

        return false;
    }

    /// <inheritdoc />
    public void OpenFileLocation(string fullPath)
    {
        var start = new ProcessStartInfo("xdg-open")
        {
            UseShellExecute = false
        };
        start.ArgumentList.Add(Path.GetDirectoryName(Path.GetFullPath(fullPath)) ?? "/");
        Process.Start(start)?.Dispose();
    }
}