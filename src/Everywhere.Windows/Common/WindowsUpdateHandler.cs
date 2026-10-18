using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Everywhere.Common;
using Everywhere.Interop;
using Serilog;

namespace Everywhere.Windows.Common;

public sealed partial class WindowsUpdateHandler(INativeHelper nativeHelper) : IPlatformUpdateHandler
{
    public string OsIdentifier => "Windows-x64";

    public UpdateAssetMetadata? SelectAsset(IEnumerable<UpdateAssetMetadata> assets, string versionString)
    {
        var assetNameSuffix = nativeHelper.IsInstalled ? $"-{OsIdentifier}-Setup-v{versionString}.exe" : $"-{OsIdentifier}-v{versionString}.zip";
        return assets.FirstOrDefault(a => a.Name.EndsWith(assetNameSuffix, StringComparison.OrdinalIgnoreCase));
    }

    public string GetDownloadType()
    {
        return nativeHelper.IsInstalled ? "setup" : "zip";
    }

    public Task ExecuteUpdateAsync(string assetPath, CancellationToken cancellationToken)
    {
        if (!assetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateViaPortableAsync(assetPath, cancellationToken);
        }

        Process.Start(new ProcessStartInfo(assetPath) { UseShellExecute = true });
        // Setup coordinates normal shutdown after the user confirms installation.
        // Cancelling UAC or the wizard must leave the running application intact.
        return Task.CompletedTask;
    }

    public bool TryParseUpdatePackageVersion(string fileName, out SemanticVersion? version)
    {
        var match = VersionRegex().Match(fileName);
        if (match.Success && SemanticVersion.TryParse(match.Groups["version"].Value, out version))
        {
            return true;
        }

        version = null;
        return false;
    }

    private static async Task UpdateViaPortableAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The application executable path is unavailable.");
        var startInfo = await PreparePortableUpdateAsync(zipPath, executablePath, cancellationToken);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("Failed to start the portable updater.");
        }
        catch
        {
            DeletePreparationDirectory(startInfo.WorkingDirectory);
            throw;
        }

        // Keep Installing active until the helper exits, preventing another update from starting.
        // After handoff, the helper owns shutdown, recovery files and failure UI. Do not cancel
        // this wait or rethrow its reported failure as a second Toast.
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
        {
            Log.Warning("The portable updater exited with code {ExitCode}. Recovery files: {Directory}.", process.ExitCode, startInfo.WorkingDirectory);
        }
    }

    private static async Task<ProcessStartInfo> PreparePortableUpdateAsync(string zipPath, string executablePath, CancellationToken cancellationToken)
    {
        var applicationDirectory = Path.GetDirectoryName(executablePath) ??
            throw new InvalidOperationException("The application directory is unavailable.");
        var parentDirectory = Directory.GetParent(applicationDirectory) ??
            throw new InvalidOperationException("Updating an application in a drive root is unsupported.");
        // Keep staging and backup on the same volume as the application so replacement is a rename.
        var workspace = Path.Combine(parentDirectory.FullName, $".Everywhere-update-{Guid.NewGuid():N}");
        var stagingDirectory = Path.Combine(workspace, "new");
        var scriptPath = Path.Combine(workspace, "update.ps1");
        var failureMessage = string.Format(LocaleKey.Windows_PortableUpdate_Failed.I18N(), workspace);

        try
        {
            Directory.CreateDirectory(stagingDirectory);
            // Extraction can be slow; keep the UI responsive while the old version is still intact.
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, stagingDirectory), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(stagingDirectory, Path.GetFileName(executablePath))))
            {
                throw new InvalidDataException("The update package does not contain the application executable at its root.");
            }

            await using var resource =
                typeof(WindowsUpdateHandler).Assembly.GetManifestResourceStream("Everywhere.Windows.Scripts.PortableUpdate.ps1") ??
                throw new InvalidOperationException("The portable update script is missing.");
            using var reader = new StreamReader(resource);
            var script = await reader.ReadToEndAsync(cancellationToken);
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(true), cancellationToken);

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workspace,
                ArgumentList =
                {
                    "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath,
                    "-ApplicationPath", executablePath,
                    "-FailureMessage", failureMessage
                }
            };

            return startInfo;
        }
        catch
        {
            DeletePreparationDirectory(workspace);
            throw;
        }
    }

    private static void DeletePreparationDirectory(string workspace)
    {
        try
        {
            if (Directory.Exists(workspace)) Directory.Delete(workspace, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(exception, "Could not clean portable update preparation directory {Directory}.", workspace);
        }
    }

    [GeneratedRegex(
        @"-v(?<version>\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?)\.(exe|zip)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        "zh-CN")]
    private static partial Regex VersionRegex();
}
