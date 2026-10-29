using System.Runtime.InteropServices;
using System.Text.Json;
using Everywhere.Common;

namespace Everywhere.Linux.Common;

/// <summary>
/// Discovers Linux releases while leaving installation to the terminal package manager.
/// </summary>
public sealed class LinuxUpdateHandler(IHttpClientFactory httpClientFactory) : IPlatformUpdateHandler, ITerminalUpdateHandler
{
    /// <summary>
    /// Identifies the explicitly selected fork release source until official Linux publication is available.
    /// </summary>
    public const string ReleaseRepository = "pasical/Everywhere";

    /// <inheritdoc />
    public string OsIdentifier => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "Linux-x64",
        Architecture.Arm64 => "Linux-arm64",
        _ => throw new PlatformNotSupportedException("No Everywhere package is published for this architecture.")
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<TerminalUpdateRelease>> GetReleasesAsync(UpdateChannel channel, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient();
        var releases = new List<TerminalUpdateRelease>();
        SemanticVersion? latestVersion = null;
        for (var page = 1; page <= 10; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://api.github.com/repos/{ReleaseRepository}/releases?per_page=100&page={page}");
            request.Headers.UserAgent.ParseAdd("Everywhere-Linux-Updater");
            using var response = await client.SendAsync(request, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Expected a GitHub release array.");
            }

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.GetProperty("draft").GetBoolean())
                {
                    continue;
                }
                var tag = item.GetProperty("tag_name").GetString();
                if (tag is null || !SemanticVersion.TryParse(tag.TrimStart('v'), out var version) || version.Channel != channel)
                {
                    continue;
                }
                if (item.GetProperty("prerelease").GetBoolean() != (channel != UpdateChannel.Stable))
                {
                    continue;
                }
                if (latestVersion is null || version > latestVersion)
                {
                    latestVersion = version;
                }
                var assetName = $"Everywhere-{OsIdentifier}-v{version}.tar.zst";
                var assets = item.GetProperty("assets").EnumerateArray()
                    .Where(asset => asset.GetProperty("name").GetString() == assetName &&
                                    asset.GetProperty("state").GetString() == "uploaded")
                    .ToArray();
                if (assets.Length != 1)
                {
                    continue;
                }
                var asset = assets[0];
                var url = $"https://github.com/{ReleaseRepository}/releases/download/{tag}/{assetName}";
                if (asset.GetProperty("browser_download_url").GetString() != url)
                {
                    throw new InvalidDataException("Release asset URL differs from the selected source.");
                }
                var size = asset.GetProperty("size").GetInt64();
                if (size <= 0)
                {
                    throw new InvalidDataException("Release asset is empty.");
                }
                // Discovery never executes this archive. The terminal validates the native package's digest.
                var digest = asset.TryGetProperty("digest", out var digestValue) ? digestValue.GetString() ?? string.Empty : string.Empty;
                var metadata = new SoftwareUpdateMetadata(channel, tag, version,
                    item.GetProperty("published_at").GetDateTimeOffset(),
                    new SoftwareUpdateMetadata.UpdateAsset(assetName, digest, size, url, url));
                var notes = item.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
                releases.Add(new TerminalUpdateRelease(metadata,
                    new Uri($"https://github.com/{ReleaseRepository}/releases/tag/{tag}"), notes));
            }
            if (document.RootElement.GetArrayLength() < 100)
            {
                if (latestVersion is not null && !releases.Any(release => release.Metadata.Version == latestVersion))
                {
                    throw new InvalidDataException("The latest release has no complete Linux archive. Retry after publication finishes.");
                }
                return releases;
            }
        }
        throw new InvalidDataException("Release history exceeded the complete scan limit.");
    }

    /// <inheritdoc />
    public UpdateAssetMetadata? SelectAsset(IEnumerable<UpdateAssetMetadata> assets, string versionString) =>
        assets.FirstOrDefault(asset => asset.Name == $"Everywhere-{OsIdentifier}-v{versionString}.tar.zst");

    /// <inheritdoc />
    public string GetDownloadType() => "tar.zst";

    /// <inheritdoc />
    public Task ExecuteUpdateAsync(string assetPath, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Run ew update from a terminal; graphical installation is disabled on Linux.");

    /// <inheritdoc />
    public bool TryParseUpdatePackageVersion(string fileName, out SemanticVersion? version)
    {
        version = null;
        return false;
    }
}
