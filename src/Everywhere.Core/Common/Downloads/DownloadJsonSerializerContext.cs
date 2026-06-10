using System.Text.Json.Serialization;

namespace Everywhere.Common.Downloads;

[JsonSerializable(typeof(List<GitHubReleaseAsset>))]
[JsonSerializable(typeof(GitHubRelease))]
internal sealed partial class DownloadJsonSerializerContext : JsonSerializerContext;