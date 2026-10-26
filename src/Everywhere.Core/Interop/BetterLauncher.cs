using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Platform.Storage;

namespace Everywhere.Interop;

public sealed class BetterBclLauncher : ILauncher
{
    /// <summary>
    /// Gets or sets the application launcher. Platforms configure this once
    /// during startup, before creating windows or interactive flows.
    /// </summary>
    public static ILauncher Shared { get; set; } = new BetterBclLauncher();

    private BetterBclLauncher() { }

    public Task<bool> LaunchUriAsync(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);

        return uri.IsAbsoluteUri ? ExecAsync(uri.AbsoluteUri) : Task.FromResult(false);
    }

    /// <summary>
    /// This Process based implementation doesn't handle the case, when there is no app to handle link.
    /// It will still return true in this case.
    /// </summary>
    public Task<bool> LaunchFileAsync(IStorageItem storageItem)
    {
        ArgumentNullException.ThrowIfNull(storageItem);

        return storageItem.TryGetLocalPath() is { } localPath ? ExecAsync(localPath) : Task.FromResult(false);
    }

    private static async Task<bool> ExecAsync(string urlOrFile)
    {
        if (OperatingSystem.IsLinux())
        {
            return await LaunchLinuxAsync(urlOrFile).ConfigureAwait(false);
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

    private static async Task<bool> LaunchLinuxAsync(string urlOrFile)
    {
        // This fallback also serves local files. Pass the target as one argument,
        // never through a shell: OAuth query strings and file names may contain
        // spaces and shell metacharacters.
        var start = new ProcessStartInfo("xdg-open")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { urlOrFile }
        };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return false;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                return process.ExitCode == 0;
            }
            catch (OperationCanceledException)
            {
                process.Kill();
                return false;
            }
        }
        catch (Win32Exception)
        {
            return false;
        }
    }
}