using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Sources;

/// <summary>
/// Reads a static v1 repository index and its artifacts from a local filesystem directory.
/// </summary>
public sealed class LocalDirectoryModSource : IModRepositorySource
{
    /// <inheritdoc />
    public string Id { get; }

    /// <summary>
    /// Gets the full path to the repository directory containing <c>index.json</c>.
    /// </summary>
    public string RootDirectory { get; }

    /// <inheritdoc />
    public bool IsSignatureRequired { get; }

    /// <inheritdoc />
    public string? TrustedPublicKey { get; }

    private readonly Uri _indexUri;
    private readonly string _indexPath;
    private string? _effectivePublicKey;

    /// <summary>
    /// Initializes a local-directory source rooted at <paramref name="rootDirectory"/>.
    /// </summary>
    public LocalDirectoryModSource(
        string id,
        string rootDirectory,
        bool isSignatureRequired = false,
        string? trustedPublicKey = null
    )
    {
        if (!ModValidationRules.IsValidIdentifier(id))
        {
            throw new ModValidationException("InvalidSourceId", $"Invalid source identifier '{id}'.");
        }

        if (trustedPublicKey is not null)
        {
            ModSignature.ValidatePublicKey(trustedPublicKey);
        }

        Id = id;
        RootDirectory = Path.GetFullPath(rootDirectory);
        IsSignatureRequired = isSignatureRequired;
        TrustedPublicKey = trustedPublicKey;
        _effectivePublicKey = trustedPublicKey;
        _indexPath = Path.Combine(RootDirectory, ModIndexFile.FileName);
        _indexUri = new Uri(_indexPath, UriKind.Absolute);
    }

    /// <inheritdoc />
    public async Task<ModIndex> FetchIndexAsync(bool shouldForceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_indexPath))
        {
            throw new ModValidationException("MissingIndex", $"Repository index '{_indexPath}' does not exist.");
        }

        await using var stream = new FileStream(_indexPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var index = await ModIndexFile.ReadAsync(stream, _indexUri, cancellationToken).ConfigureAwait(false);
        ModArtifactVerifier.ValidateIndexTrust(index, Id, IsSignatureRequired, TrustedPublicKey);
        _effectivePublicKey = TrustedPublicKey ?? index.PublicKey;
        return index;
    }

    /// <inheritdoc />
    public async Task<Stream> OpenArtifactAsync(
        string modId,
        ModIndexVersion version,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default
    )
    {
        if (_effectivePublicKey is null && IsSignatureRequired)
        {
            await FetchIndexAsync(shouldForceRefresh: false, cancellationToken).ConfigureAwait(false);
        }

        var resolved = ModIndexFile.ResolveAssetUri(_indexUri, version.Artifact.Url);
        if (!string.Equals(resolved.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModValidationException(
                "InvalidUrl",
                $"Local directory source '{Id}' cannot fetch non-file artifact URI '{resolved}'.");
        }

        var path = resolved.LocalPath;
        if (!File.Exists(path))
        {
            throw new ModValidationException("MissingArtifact", $"Artifact file '{path}' does not exist.");
        }

        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        try
        {
            await ModArtifactVerifier.VerifyStreamAsync(
                    stream,
                    modId,
                    version,
                    IsSignatureRequired,
                    _effectivePublicKey,
                    destination: null,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
            stream.Position = 0;
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReadTextDocumentAsync(string documentReference, CancellationToken cancellationToken = default)
    {
        var resolved = ModIndexFile.ResolveAssetUri(_indexUri, documentReference);
        if (!string.Equals(resolved.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = resolved.LocalPath;
        if (!File.Exists(path)) return null;

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 16384, useAsync: true);
        return await ModArtifactVerifier.ReadBoundedUtf8Async(
                stream,
                ModArtifactVerifier.MaxDefaultTextDocumentBytes,
                documentReference,
                cancellationToken)
            .ConfigureAwait(false);
    }
}
