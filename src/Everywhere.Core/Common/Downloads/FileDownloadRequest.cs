using System.Text.Json.Serialization;

namespace Everywhere.Common;

public sealed record FileDownloadSource(string Url, string? Name = null);

public sealed record FileDownloadRequest(
    string DestinationPath,
    IReadOnlyList<FileDownloadSource> Sources,
    long? Size = null,
    string? Sha256Digest = null,
    long? BytesPerSecondLimit = null);

public interface IFileDownloadService
{
    Task<string> DownloadAsync(
        FileDownloadRequest request,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}

[JsonSerializable(typeof(List<GitHubReleaseAsset>))]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class DownloadJsonSerializerContext : JsonSerializerContext;

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset>? Assets);

internal sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest);
