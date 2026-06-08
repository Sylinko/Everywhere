using System.Diagnostics;
using System.Runtime.InteropServices;
using Everywhere.Common;

namespace Everywhere.Mac.Common;

public sealed class MacUpdateHandler : IPlatformUpdateHandler
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

    public Task ExecuteUpdateAsync(string assetPath, CancellationToken cancellationToken)
    {
        if (!assetPath.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Expected a .pkg file for macOS update.");
        }

        var psi = new ProcessStartInfo("open")
        {
            ArgumentList = { assetPath }
        };
        Process.Start(psi);
        Environment.Exit(0);

        return Task.CompletedTask;
    }
}
