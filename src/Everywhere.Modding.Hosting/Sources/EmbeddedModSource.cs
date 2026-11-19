using System.Reflection;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Sources;

/// <summary>
/// Serves a cryptographically verified built-in repository directly from host resource streams without unpacking onto disk.
/// </summary>
public sealed class EmbeddedModSource : IModRepositorySource
{
    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public bool IsSignatureRequired { get; }

    /// <inheritdoc />
    public string? TrustedPublicKey { get; }

    private readonly Func<string, Stream?> _openResource;
    private readonly Lock _gate = new();
    private ModIndex? _cachedIndex;
    private string? _effectivePublicKey;

    /// <summary>
    /// Initializes an embedded mod source backed by a caller-supplied relative resource stream factory.
    /// </summary>
    public EmbeddedModSource(
        string id,
        Func<string, Stream?> openResource,
        bool isSignatureRequired = true,
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
        _openResource = openResource;
        IsSignatureRequired = isSignatureRequired;
        TrustedPublicKey = trustedPublicKey;
        _effectivePublicKey = trustedPublicKey;
    }

    /// <summary>
    /// Creates an embedded mod source reading <see cref="Assembly"/> manifest resources under <paramref name="resourcePrefix"/>.
    /// </summary>
    public static EmbeddedModSource FromAssembly(
        string id,
        Assembly assembly,
        string resourcePrefix,
        bool isSignatureRequired = true,
        string? trustedPublicKey = null
    )
    {
        var prefix = resourcePrefix.TrimEnd('.');
        return new EmbeddedModSource(
            id,
            relativePath => OpenManifestResource(assembly, prefix, relativePath),
            isSignatureRequired,
            trustedPublicKey);
    }

    /// <inheritdoc />
    public async Task<ModIndex> FetchIndexAsync(bool shouldForceRefresh = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!shouldForceRefresh)
        {
            lock (_gate)
            {
                if (_cachedIndex is not null)
                {
                    return _cachedIndex;
                }
            }
        }

        await using var stream = _openResource(ModIndexFile.FileName) ??
            throw new ModValidationException("MissingIndex", $"Embedded source '{Id}' does not contain '{ModIndexFile.FileName}'.");

        var index = await ModIndexFile.ReadAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        ModArtifactVerifier.ValidateIndexTrust(index, Id, IsSignatureRequired, TrustedPublicKey);

        lock (_gate)
        {
            _cachedIndex = index;
            _effectivePublicKey = TrustedPublicKey ?? index.PublicKey;
        }

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
        PackagePath.ValidateRelative(version.Artifact.Url);
        var effectiveKey = await EnsureEffectivePublicKeyAsync(cancellationToken).ConfigureAwait(false);

        var stream = _openResource(version.Artifact.Url) ??
            throw new ModValidationException(
                "MissingArtifact",
                $"Embedded artifact '{version.Artifact.Url}' for '{modId}@{version.Version}' was not found.");

        if (stream.CanSeek)
        {
            try
            {
                await ModArtifactVerifier.VerifyStreamAsync(
                        stream,
                        modId,
                        version,
                        IsSignatureRequired,
                        effectiveKey,
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

        var buffered = new MemoryStream();
        try
        {
            await using (stream)
            {
                await ModArtifactVerifier.VerifyStreamAsync(
                        stream,
                        modId,
                        version,
                        IsSignatureRequired,
                        effectiveKey,
                        buffered,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            buffered.Position = 0;
            return buffered;
        }
        catch
        {
            await buffered.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string?> ReadTextDocumentAsync(string documentReference, CancellationToken cancellationToken = default)
    {
        PackagePath.ValidateRelative(documentReference);
        var stream = _openResource(documentReference);
        if (stream is null) return null;

        await using (stream)
        {
            return await ModArtifactVerifier.ReadBoundedUtf8Async(
                    stream,
                    ModArtifactVerifier.MaxDefaultTextDocumentBytes,
                    documentReference,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<string?> EnsureEffectivePublicKeyAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_cachedIndex is not null || !IsSignatureRequired || TrustedPublicKey is not null)
            {
                return _effectivePublicKey;
            }
        }

        await FetchIndexAsync(shouldForceRefresh: false, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return _effectivePublicKey;
        }
    }

    private static Stream? OpenManifestResource(Assembly assembly, string prefix, string relativePath)
    {
        var normalized = relativePath.TrimStart('/').Replace('\\', '/');
        PackagePath.ValidateRelative(normalized);

        var dotted = $"{prefix}.{normalized.Replace('/', '.')}";
        return assembly.GetManifestResourceStream(dotted) ?? assembly.GetManifestResourceStream($"{prefix}/{normalized}");
    }
}
