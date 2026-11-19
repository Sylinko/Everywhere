using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Sources;

/// <summary>
/// Abstracts reading a validated v1 mod repository index, verified artifact package streams, and documentation text
/// across embedded host resources, local directories, and HTTP mirror endpoints.
/// </summary>
public interface IModRepositorySource
{
    /// <summary>
    /// Gets the host-assigned unique identifier for this source.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets whether the index and every downloaded package from this source must carry valid ECDSA P-256 signatures.
    /// </summary>
    bool IsSignatureRequired { get; }

    /// <summary>
    /// Gets the pinned ECDSA P-256 public key when the source is pre-trusted or TOFU-pinned, or <see langword="null"/> otherwise.
    /// </summary>
    string? TrustedPublicKey { get; }

    /// <summary>
    /// Fetches and validates the source's <c>index.json</c> document.
    /// </summary>
    Task<ModIndex> FetchIndexAsync(bool shouldForceRefresh = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a readable package stream after verifying exact byte length, SHA-256 digest, and signature policy.
    /// </summary>
    Task<Stream> OpenArtifactAsync(
        string modId,
        ModIndexVersion version,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Reads a UTF-8 documentation or license file referenced by the index, returning <see langword="null"/> if not found.
    /// </summary>
    Task<string?> ReadTextDocumentAsync(string documentReference, CancellationToken cancellationToken = default);
}
