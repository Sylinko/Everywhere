using System.Text.Json.Serialization;

namespace Everywhere.Plugin.Sdk;

/// <summary>
/// Reports the identity of a successfully validated package.
/// </summary>
internal sealed record ManifestValidationResult
{
    /// <summary>
    /// Gets the stable result code consumed by author tooling.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code => "ManifestValid";

    /// <summary>
    /// Gets the validated plugin ID.
    /// </summary>
    [JsonPropertyName("plugin")]
    public required string Plugin { get; init; }

    /// <summary>
    /// Gets the validated plugin version.
    /// </summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }
}

/// <summary>
/// Reports the completed package archive.
/// </summary>
internal sealed record PackageCreationResult
{
    /// <summary>
    /// Gets the stable result code consumed by author tooling.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code => "PackageCreated";

    /// <summary>
    /// Gets the absolute path to the generated ZIP.
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }
}

/// <summary>
/// Reports a failed SDK operation without serializing exception objects.
/// </summary>
internal sealed record SdkErrorResult
{
    /// <summary>
    /// Gets the validation code or the general SDK failure code.
    /// </summary>
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    /// <summary>
    /// Gets the diagnostic text for the failed operation.
    /// </summary>
    [JsonPropertyName("message")]
    public required string Message { get; init; }
}