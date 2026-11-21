using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
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
        if (Application.Current is not App app)
        {
            throw new InvalidOperationException("The application instance is unavailable for update handoff.");
        }

        if (!assetPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateViaPortableAsync(app, assetPath, cancellationToken);
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(assetPath)
            {
                Arguments = "/SILENT /CLOSEAPPLICATIONS /LAUNCH",
                UseShellExecute = true
            }) ?? throw new InvalidOperationException("Failed to start the setup installer.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User declined the UAC prompt; keep the running application and ready package intact.
            Log.Information("User cancelled elevation for installer {InstallerPath}.", assetPath);
            return Task.CompletedTask;
        }

        app.Shutdown();
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

    private static async Task UpdateViaPortableAsync(App app, string zipPath, CancellationToken cancellationToken = default)
    {
        var executablePath = Environment.ProcessPath ?? throw new InvalidOperationException("The application executable path is unavailable.");
        var startInfo = await PreparePortableUpdateAsync(zipPath, executablePath, Environment.ProcessId, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start the portable updater.");
        }
        catch
        {
            DeletePreparationDirectory(startInfo.WorkingDirectory);
            throw;
        }

        app.Shutdown();
    }

    private static async Task<ProcessStartInfo> PreparePortableUpdateAsync(
        string zipPath,
        string executablePath,
        int parentProcessId,
        CancellationToken cancellationToken)
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
                    "-FailureMessage", failureMessage,
                    "-ParentProcessId", parentProcessId.ToString(CultureInfo.InvariantCulture)
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
