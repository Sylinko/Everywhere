using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Everywhere.Common;

namespace Everywhere.Linux.Common;

public sealed partial class LinuxUpdateHandler : IPlatformUpdateHandler
{
    public string OsIdentifier => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";

    private static string OsString => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "Linux-arm64" : "Linux-x64";

    private static string OsDistro
    {
        get
        {
            string[] paths = ["/etc/os-release", "/usr/lib/os-release"];
            foreach (var path in paths)
            {
                if (!File.Exists(path))
                    continue;
                var lines = File.ReadAllLines(path);
                foreach (var line in lines)
                {
                    if (!line.StartsWith("ID=", StringComparison.Ordinal)) continue;
                    var id = line.Substring(3).Trim();
                    if (id.StartsWith('"') && id.EndsWith('"') && id.Length >= 2)
                        id = id.Substring(1, id.Length - 2);
                    return id;
                }
            }
            return "unknown";
        }
    }

    private static string OsPackageType
    {
        get
        {
            var distro = OsDistro;
            return distro switch
            {
                "debian" or "ubuntu" or "linuxmint" or "kali" => "deb",
                "rhel" or "centos" or "fedora" => "rpm",
                _ => ""
            };
        }
    }

    public UpdateAssetMetadata? SelectAsset(IEnumerable<UpdateAssetMetadata> assets, string versionString)
    {
        var assetType = OsPackageType;
        if (string.IsNullOrWhiteSpace(assetType))
        {
            return null; // Unsupported package type
        }
        var assetNameSuffix = $"-{OsString}-v{versionString}.{assetType}";

        return assets.FirstOrDefault(a => a.Name.EndsWith(assetNameSuffix, StringComparison.OrdinalIgnoreCase));
    }

    public string GetDownloadType()
    {
        return OsPackageType;
    }

    public Task ExecuteUpdateAsync(string assetPath, CancellationToken cancellationToken)
    {
        try
        {
            if (!OperatingSystem.IsLinux())
                return Task.CompletedTask;

            switch (OsPackageType)
            {
                case "deb":
                    Process.Start(
                        new ProcessStartInfo("sudo", $"dpkg -i \"{assetPath}\"") { UseShellExecute = true }
                        )?.WaitForExit();
                    break;
                case "rpm":
                    // Todo: Needs rpm installation implementation
                    break;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to install package.", ex);
        }

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

    [GeneratedRegex(@"-v(?<version>\d+\.\d+\.\d+(?:\.\d+)?(?:-[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)*)?)\.(deb|rpm)$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "zh-CN")]
    private static partial Regex VersionRegex();
}
