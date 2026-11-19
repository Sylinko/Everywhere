using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Hosting.Sources;

/// <summary>
/// Enforces index trust policies, bounded streaming hash/size verification, and document size limits across all source types.
/// </summary>
internal static class ModArtifactVerifier
{
    /// <summary>
    /// Limits remotely fetched README and license text documents to 1 MiB.
    /// </summary>
    internal const int MaxDefaultTextDocumentBytes = 1024 * 1024;

    /// <summary>
    /// Verifies that a parsed index complies with the source's pinned public key and signature requirement.
    /// </summary>
    internal static void ValidateIndexTrust(ModIndex index, string sourceId, bool isSignatureRequired, string? trustedPublicKey)
    {
        if (trustedPublicKey is not null && !string.Equals(index.PublicKey, trustedPublicKey, StringComparison.Ordinal))
        {
            throw new ModValidationException(
                "PublicKeyMismatch",
                $"Index '{index.Id}' public key does not match the pinned key for source '{sourceId}'.");
        }

        if (isSignatureRequired && (string.IsNullOrWhiteSpace(index.PublicKey) || string.IsNullOrWhiteSpace(index.Signature)))
        {
            throw new ModValidationException("UntrustedIndex", $"Source '{sourceId}' requires a cryptographically signed index.");
        }
    }

    /// <summary>
    /// Reads <paramref name="source"/> in streaming chunks, aborting immediately if size exceeds the declared limit,
    /// verifies exact byte count, SHA-256 digest, and optional/required artifact signature, optionally copying to <paramref name="destination"/>.
    /// </summary>
    internal static async Task VerifyStreamAsync(
        Stream source,
        string modId,
        ModIndexVersion version,
        bool isSignatureRequired,
        string? effectivePublicKey,
        Stream? destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken
    )
    {
        var expectedSize = version.Artifact.Size;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        long totalBytes = 0;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                totalBytes += bytesRead;
                if (totalBytes > expectedSize)
                {
                    throw new ModValidationException(
                        "ArtifactSizeMismatch",
                        $"Artifact '{modId}@{version.Version}' exceeded declared size {expectedSize} bytes.");
                }

                hash.AppendData(buffer.AsSpan(0, bytesRead));
                if (destination is not null)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                }

                if (expectedSize > 0)
                {
                    progress?.Report(Math.Clamp((double)totalBytes / expectedSize, 0d, 1d));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (totalBytes != expectedSize)
        {
            throw new ModValidationException(
                "ArtifactSizeMismatch",
                $"Artifact '{modId}@{version.Version}' length {totalBytes} does not match expected size {expectedSize}.");
        }

        var actualSha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (!string.Equals(actualSha256, version.Artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new ModValidationException(
                "ArtifactHashMismatch",
                $"Artifact '{modId}@{version.Version}' SHA-256 mismatch: expected '{version.Artifact.Sha256}', got '{actualSha256}'.");
        }

        var signature = version.Artifact.Signature;
        var shouldVerifySignature = isSignatureRequired || !string.IsNullOrWhiteSpace(signature);
        if (shouldVerifySignature)
        {
            if (string.IsNullOrWhiteSpace(effectivePublicKey) ||
                string.IsNullOrWhiteSpace(signature) ||
                !ModSignature.VerifyArtifact(modId, version.Version, actualSha256, totalBytes, signature, effectivePublicKey))
            {
                throw new ModValidationException(
                    "InvalidSignature",
                    $"Artifact '{modId}@{version.Version}' failed cryptographic signature verification.");
            }
        }
    }

    /// <summary>
    /// Reads bounded raw bytes from an index or text response stream without buffering unbounded payloads.
    /// </summary>
    internal static async Task<byte[]> ReadBoundedBytesAsync(
        Stream stream,
        long maxBytes,
        string reference,
        CancellationToken cancellationToken
    )
    {
        using var memory = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        long totalBytes = 0;

        try
        {
            while (true)
            {
                var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (bytesRead == 0) break;

                totalBytes += bytesRead;
                if (totalBytes > maxBytes)
                {
                    throw new ModValidationException("PayloadTooLarge", $"Payload '{reference}' exceeds limit of {maxBytes} bytes.");
                }

                memory.Write(buffer, 0, bytesRead);
            }

            return memory.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Reads a bounded UTF-8 document stream such as README Markdown or license notice text.
    /// </summary>
    internal static async Task<string> ReadBoundedUtf8Async(
        Stream stream,
        long maxBytes,
        string reference,
        CancellationToken cancellationToken
    )
    {
        var bytes = await ReadBoundedBytesAsync(stream, maxBytes, reference, cancellationToken).ConfigureAwait(false);
        return Encoding.UTF8.GetString(bytes);
    }
}
