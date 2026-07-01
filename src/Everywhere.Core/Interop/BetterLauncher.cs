using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Avalonia.Platform.Storage;

namespace Everywhere.Interop;

public sealed partial class BetterBclLauncher : ILauncher
{
    public static ILauncher Shared { get; } = new BetterBclLauncher();

    private BetterBclLauncher() { }

    public Task<bool> LaunchUriAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return Task.FromResult(uri.IsAbsoluteUri && Exec(uri.AbsoluteUri));
    }

    /// <summary>
    /// This Process based implementation doesn't handle the case, when there is no app to handle link.
    /// It will still return true in this case.
    /// </summary>
    public Task<bool> LaunchFileAsync(IStorageItem storageItem)
    {
        ArgumentNullException.ThrowIfNull(storageItem);

        return storageItem.TryGetLocalPath() is { } localPath ? Task.FromResult(Exec(localPath)) : Task.FromResult(false);
    }

    private static bool Exec(string urlOrFile)
    {
        if (OperatingSystem.IsLinux())
        {
            // If no associated application/json MimeType is found xdg-open opens return error
            // but it tries to open it anyway using the console editor (nano, vim, other..)
            ShellExec($"xdg-open {urlOrFile}");
            return true;
        }

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo
            {
                FileName = OperatingSystem.IsWindows() ? urlOrFile : "open",
                CreateNoWindow = true,
                UseShellExecute = OperatingSystem.IsWindows()
            };

            if (OperatingSystem.IsMacOS())
            {
                psi.ArgumentList.Add(urlOrFile); // Use ArgumentList to avoid issues with spaces/special characters
            }

            try
            {
                using var process = Process.Start(psi);
            }
            catch (Win32Exception e) when (OperatingSystem.IsWindows() && e.NativeErrorCode == -2147221003)
            {
                // ERROR_NO_ASSOCIATION: No application is associated with the specified file for this operation.
                // Fall back to explorer to select the file
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
                    ArgumentList = { "/select,", urlOrFile },
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }

            return true;
        }

        return false;
    }

    private static void ShellExec(string cmd)
    {
        var escapedArgs = ShellEscapeRegex().Replace(cmd, "\\").Replace("\"", "\\\\\\\"");
        using var process = Process.Start(
            new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"{escapedArgs}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            }
        );
    }

    [GeneratedRegex("(?=[`~!#&*()|;'<>])")]
    private static partial Regex ShellEscapeRegex();
}