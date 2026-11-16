using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using Everywhere.Common;

namespace Everywhere.Mac.Common;

public sealed partial class MacUpdateHandler : IPlatformUpdateHandler
{
    public string OsIdentifier => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macOS-arm64" : "macOS-x64";

    public UpdateAssetMetadata? SelectAsset(IEnumerable<UpdateAssetMetadata> assets, string versionString)
    {
        var assetNameSuffix = $"-{OsIdentifier}-v{versionString}.pkg";
        return assets.FirstOrDefault(a => a.Name.EndsWith(assetNameSuffix, StringComparison.OrdinalIgnoreCase));
    }

    public string GetDownloadType()
    {
        return "pkg";
    }

    /// <inheritdoc />
    public async Task ExecuteUpdateAsync(string assetPath, CancellationToken cancellationToken)
    {
        if (!assetPath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected a .pkg file for macOS update.");
        }

        var workspace = NSWorkspace.SharedWorkspace;
        using var installerUrl = workspace.UrlForApplication("com.apple.installer") ??
            throw new InvalidOperationException("Could not locate the macOS Installer application.");
        using var packageUrl = NSUrl.FromFilename(assetPath);
        using var configuration = NSWorkspaceOpenConfiguration.Create();

        // Wait for Installer to accept the package before requesting a normal app shutdown.
        using var installer = await workspace.OpenUrlsAsync([packageUrl], installerUrl, configuration);
        await Dispatcher.UIThread.InvokeAsync(() => NSApplication.SharedApplication.Terminate(null));
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

    [GeneratedRegex(@"-v(?<version>\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?)\.pkg$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex VersionRegex();
}
