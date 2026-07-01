using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using Everywhere.Common;
using Everywhere.Interop;

namespace Everywhere.Windows.Common;

public sealed partial class WindowsUpdateHandler(INativeHelper nativeHelper) : IPlatformUpdateHandler
{
    public string OsIdentifier => "Windows-x64";

    public UpdateAssetMetadata? SelectAsset(IEnumerable<UpdateAssetMetadata> assets, string versionString)
    {
        var assetNameSuffix = nativeHelper.IsInstalled ?
            $"-{OsIdentifier}-Setup-v{versionString}.exe" :
            $"-{OsIdentifier}-v{versionString}.zip";

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
        Environment.Exit(0);
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
        var scriptPath = Path.Combine(Path.GetTempPath(), "update.bat");
        var exeLocation = Assembly.GetExecutingAssembly().Location;
        var currentDir = Path.GetDirectoryName(exeLocation)!;

        var scriptContent =
            $"""
             @echo off
             ECHO Waiting for the application to close...
             TASKKILL /IM "{Path.GetFileName(exeLocation)}" /F >nul 2>nul
             timeout /t 2 /nobreak >nul
             ECHO Backing up old version...
             ren "{currentDir}" "{Path.GetFileName(currentDir)}_old"
             ECHO Unpacking new version...
             powershell -Command "Expand-Archive -LiteralPath '{zipPath}' -DestinationPath '{currentDir}' -Force"
             IF %ERRORLEVEL% NEQ 0 (
                 ECHO Unpacking failed, restoring old version...
                 ren "{Path.Combine(Path.GetDirectoryName(currentDir)!, Path.GetFileName(currentDir) + "_old")}" "{Path.GetFileName(currentDir)}"
                 GOTO END
             )
             ECHO Cleaning up old files...
             rd /s /q "{Path.Combine(Path.GetDirectoryName(currentDir)!, Path.GetFileName(currentDir) + "_old")}"
             ECHO Starting new version...
             start "" "{exeLocation}"
             :END
             del "{scriptPath}"
             """;

        await File.WriteAllTextAsync(scriptPath, scriptContent, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true, Verb = "runas" });
        Environment.Exit(0);
    }

    [GeneratedRegex(@"-v(?<version>\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?)\.(exe|zip)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex VersionRegex();
}
