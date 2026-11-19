using System.Text.Json.Serialization;

namespace Everywhere.Modding.Sdk;

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
    /// Gets the validated mod ID.
    /// </summary>
    [JsonPropertyName("mod")]
    public required string Mod { get; init; }

    /// <summary>
    /// Gets the validated mod version.
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
/// Reports a generated ECDSA P-256 signing key pair.
/// </summary>
internal sealed record KeyPairGenerationResult
{
    /// <summary>
    /// Gets the stable result code consumed by author tooling.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code => "KeyPairGenerated";

    /// <summary>
    /// Gets the formatted ECDSA P-256 public key string.
    /// </summary>
    [JsonPropertyName("publicKey")]
    public required string PublicKey { get; init; }
}

/// <summary>
/// Reports a generated repository index document.
/// </summary>
internal sealed record IndexCreationResult
{
    /// <summary>
    /// Gets the stable result code consumed by author tooling.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code => "IndexCreated";

    /// <summary>
    /// Gets the absolute path to the generated <c>index.json</c> file.
    /// </summary>
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    /// <summary>
    /// Gets the number of distinct mods listed in the generated index.
    /// </summary>
    [JsonPropertyName("modCount")]
    public required int ModCount { get; init; }
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
