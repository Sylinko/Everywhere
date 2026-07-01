using System.Diagnostics;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Common;
using Everywhere.Configuration;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Views;

public partial class DebugFeaturesControl(ILogger<DebugFeaturesControl> logger) : TemplatedControl
{
    [RelayCommand]
    private async Task EditSettingsFileAsync()
    {
        try
        {
            var settingsPath = Path.Combine(RuntimeConstants.WritableFolderPath, "settings.json");
            var launched = await App.Launcher.LaunchFileInfoAsync(new FileInfo(settingsPath));
            if (!launched)
            {
                throw new InvalidOperationException($"Unable to launch: {settingsPath}");
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogError(ex, "Failed to open settings file.");
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }

    [RelayCommand]
    private async Task OpenLogsFolderAsync()
    {
        try
        {
            var logsPath = RuntimeConstants.EnsureWritableDataFolderPath("logs");
            var launched = await App.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(logsPath));
            if (!launched)
            {
                throw new InvalidOperationException($"Unable to launch: {logsPath}");
            }
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogError(ex, "Failed to open logs folder.");
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }

    [RelayCommand]
    private async Task CreateDumpAsync()
    {
        try
        {
#if WINDOWS
            // Use createdump.exe to create a dump of the current process.
            var fileName = Path.GetFullPath(
                Path.Combine(
                    Path.GetDirectoryName(Environment.ProcessPath ?? typeof(DebugFeaturesControl).Assembly.Location) ?? ".",
                    "createdump.exe"));
            var dumpsPath = RuntimeConstants.EnsureWritableDataFolderPath("dumps");
            var dumpPath = Path.Combine(dumpsPath, $"dump_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.dmp");
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = $"--full -f {dumpPath}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            // toast: Everywhere will freeze for a few seconds while the dump is being created.
            ToastManager.Warning(LocaleResolver.Common_Warning, LocaleResolver.DebugFeaturesControl_CreateDumpToast_Content, durationSeconds: 3d);
            await Task.Delay(500); // Give the toast time to show before freezing the UI.

            var process = Process.Start(psi);
            if (process is null)
            {
                throw new InvalidOperationException("Failed to start createdump process. Maybe createdump.exe is missing?");
            }

            var error = await process.StandardError.ReadToEndAsync();
            if (!string.IsNullOrWhiteSpace(error))
            {
                logger.LogWarning("createdump error output: {Error}", error);
            }

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
            {
                // The dump may still have been created despite the non-zero exit code.
                ToastManager.Error(LocaleResolver.Common_Error, error.Trim());

                if (!File.Exists(dumpPath))
                {
                    return;
                }
            }

            await App.Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(dumpsPath));
#endif
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            logger.LogError(ex, "Failed to create dump.");
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());
        }
    }
}
