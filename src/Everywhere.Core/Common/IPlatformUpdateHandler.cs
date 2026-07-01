using System.Text.Json.Serialization;

namespace Everywhere.Common;

public interface IPlatformUpdateHandler
{
    /// <summary>
    /// Gets the OS identifier value for constructing API URL (e.g. win-x64, linux-x64, osx-x64).
    /// </summary>
    string OsIdentifier { get; }
    
    /// <summary>
    /// Uses platform specific rules to find the appropriate asset metadata to download for the given version.
    /// </summary>
    UpdateAssetMetadata? SelectAsset(IEnumerable<UpdateAssetMetadata> assets, string versionString);

    /// <summary>
    /// Gets the OS type value for constructing ghproxy download URL (e.g. zip, setup, pkg, deb).
    /// </summary>
    string GetDownloadType();

    /// <summary>
    /// Executes the downloaded asset to perform the update.
    /// </summary>
    Task ExecuteUpdateAsync(string assetPath, CancellationToken cancellationToken);
    
    /// <summary>
    /// Checks if a file from the updates folder matches a platform-specific update package and returns its version.
    /// </summary>
    bool TryParseUpdatePackageVersion(string fileName, out SemanticVersion? version);
}

[Serializable]
public record UpdateAssetMetadata(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("size")] long Size
);

[JsonSerializable(typeof(List<UpdateAssetMetadata>))]
public partial class UpdateAssetMetadataJsonSerializerContext : JsonSerializerContext;
