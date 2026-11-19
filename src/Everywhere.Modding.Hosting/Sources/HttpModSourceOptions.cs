namespace Everywhere.Modding.Hosting.Sources;

/// <summary>
/// Configures endpoints, staggered mirror racing, caching, and trust requirements for an HTTP mod source.
/// </summary>
public sealed record HttpModSourceOptions
{
    /// <summary>
    /// Gets the host-assigned unique identifier for this source.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Gets the primary static <c>index.json</c> HTTP(S) URI.
    /// </summary>
    public required Uri PrimaryIndexUri { get; init; }

    /// <summary>
    /// Gets optional mirror <c>index.json</c> HTTP(S) URIs tried alongside the primary endpoint.
    /// </summary>
    public IReadOnlyList<Uri> MirrorIndexUris { get; init; } = [];

    /// <summary>
    /// Gets the optional directory used for persisted ETag index caches and streamed download staging.
    /// </summary>
    public string? CacheDirectory { get; init; }

    /// <summary>
    /// Gets whether the index and every package artifact must carry valid ECDSA P-256 signatures.
    /// </summary>
    public bool IsSignatureRequired { get; init; }

    /// <summary>
    /// Gets the optional pinned ECDSA P-256 public key for this source.
    /// </summary>
    public string? TrustedPublicKey { get; init; }

    /// <summary>
    /// Gets the Happy-Eyeballs stagger delay before launching subsequent mirror probes when the primary has not responded.
    /// </summary>
    public TimeSpan StaggerDelay { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>
    /// Gets how long a fetched in-memory index snapshot is reused without issuing an HTTP request.
    /// </summary>
    public TimeSpan CacheTtl { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Gets the maximum allowed byte length of a remote <c>index.json</c> response.
    /// </summary>
    public long MaxIndexBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>
    /// Gets the maximum allowed byte length of a remote documentation or license text response.
    /// </summary>
    public long MaxTextDocumentBytes { get; init; } = ModArtifactVerifier.MaxDefaultTextDocumentBytes;
}
