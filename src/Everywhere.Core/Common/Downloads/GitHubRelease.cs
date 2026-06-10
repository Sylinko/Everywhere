using System.Text.Json.Serialization;

namespace Everywhere.Common.Downloads;

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string? TagName,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset>? Assets);