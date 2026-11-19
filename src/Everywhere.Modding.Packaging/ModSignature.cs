using System.Security.Cryptography;
using System.Text;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Provides zero-dependency ECDSA P-256 signing and verification for repository indexes and package artifacts.
/// </summary>
public static class ModSignature
{
    /// <summary>
    /// Gets the algorithm prefix used in serialized public keys and signatures.
    /// </summary>
    public const string AlgorithmPrefix = "ecdsa-p256:";

    private const int P256SignatureLength = 64;
    private const string NistP256Oid = "1.2.840.10045.3.1.7";

    /// <summary>
    /// Generates a new ECDSA P-256 signing key pair in PKCS#8 PEM and formatted public-key string forms.
    /// </summary>
    public static (string PrivateKeyPem, string PublicKey) GenerateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), FormatPublicKey(ecdsa));
    }

    /// <summary>
    /// Derives the formatted public key string corresponding to a PKCS#8 ECDSA P-256 private key PEM.
    /// </summary>
    public static string GetPublicKey(ReadOnlySpan<char> privateKeyPem)
    {
        using var ecdsa = ImportPrivateKey(privateKeyPem);
        return FormatPublicKey(ecdsa);
    }

    /// <summary>
    /// Validates that a public key string encodes a valid ECDSA P-256 SubjectPublicKeyInfo payload.
    /// </summary>
    public static void ValidatePublicKey(string publicKey)
    {
        using var _ = ImportPublicKey(publicKey);
    }

    /// <summary>
    /// Validates the textual format and byte length of an <c>ecdsa-p256:</c> signature string.
    /// </summary>
    public static void ValidateSignatureFormat(string signature)
    {
        if (!TryDecodeSignature(signature, out _))
        {
            throw new ModValidationException("InvalidSignature", "Signature must use 'ecdsa-p256:<base64>' with a 64-byte P-256 signature.");
        }
    }

    /// <summary>
    /// Signs a package artifact's identity, exact version, SHA-256 digest, and byte size.
    /// </summary>
    public static string SignArtifact(string modId, string version, string sha256Hex, long size, ReadOnlySpan<char> privateKeyPem)
    {
        var payload = CreateArtifactPayload(modId, version, sha256Hex, size);
        using var ecdsa = ImportPrivateKey(privateKeyPem);
        var signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return AlgorithmPrefix + Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifies a package artifact signature against a trusted ECDSA P-256 public key.
    /// </summary>
    public static bool VerifyArtifact(
        string modId,
        string version,
        string sha256Hex,
        long size,
        string signature,
        string publicKey
    )
    {
        using var ecdsa = ImportPublicKey(publicKey);
        if (!TryDecodeSignature(signature, out var signatureBytes))
        {
            return false;
        }

        var payload = CreateArtifactPayload(modId, version, sha256Hex, size);
        return ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>
    /// Signs the canonical serialized payload of a <see cref="ModIndex"/> document.
    /// </summary>
    public static string SignIndex(ModIndex index, ReadOnlySpan<char> privateKeyPem)
    {
        using var ecdsa = ImportPrivateKey(privateKeyPem);
        var payload = ModIndexFile.CreateSigningPayload(index);
        var signature = ecdsa.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return AlgorithmPrefix + Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifies an index document's signature against a trusted ECDSA P-256 public key.
    /// </summary>
    public static bool VerifyIndex(ModIndex index, string publicKey)
    {
        using var ecdsa = ImportPublicKey(publicKey);
        if (index.Signature is null || !TryDecodeSignature(index.Signature, out var signatureBytes))
        {
            return false;
        }

        var payload = ModIndexFile.CreateSigningPayload(index);
        return ecdsa.VerifyData(payload, signatureBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    private static byte[] CreateArtifactPayload(string modId, string version, string sha256Hex, long size)
    {
        if (!ModValidationRules.IsValidIdentifier(modId))
        {
            throw new ModValidationException("InvalidMod", $"Invalid mod ID '{modId}'.");
        }

        var normalizedVersion = ModCompatibility.ParseVersion(version).ToNormalizedString();
        if (!ModIndexFile.IsValidSha256Hex(sha256Hex))
        {
            throw new ModValidationException("InvalidArtifact", $"Invalid SHA-256 digest '{sha256Hex}'.");
        }

        if (size <= 0)
        {
            throw new ModValidationException("InvalidArtifact", "Artifact size must be greater than zero.");
        }

        return Encoding.UTF8.GetBytes($"everywhere-mod-artifact-v1\n{modId}\n{normalizedVersion}\n{sha256Hex.ToLowerInvariant()}\n{size}");
    }

    private static string FormatPublicKey(ECDsa ecdsa) => AlgorithmPrefix + Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

    private static ECDsa ImportPrivateKey(ReadOnlySpan<char> privateKeyPem)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportFromPem(privateKeyPem);
            if (!IsNistP256(ecdsa))
            {
                throw new ModValidationException("InvalidPrivateKey", "Private key must use the ECDSA P-256 curve.");
            }

            return ecdsa;
        }
        catch (Exception exception) when (exception is not ModValidationException)
        {
            ecdsa.Dispose();
            throw new ModValidationException("InvalidPrivateKey", $"Failed to parse ECDSA P-256 private key PEM: {exception.Message}");
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static ECDsa ImportPublicKey(string publicKey)
    {
        if (string.IsNullOrEmpty(publicKey) || !publicKey.StartsWith(AlgorithmPrefix, StringComparison.Ordinal))
        {
            throw new ModValidationException("InvalidPublicKey", "Public key must start with 'ecdsa-p256:'.");
        }

        var encoded = publicKey.AsSpan(AlgorithmPrefix.Length);
        if (encoded.IsEmpty)
        {
            throw new ModValidationException("InvalidPublicKey", "Public key payload is empty.");
        }

        var buffer = new byte[(encoded.Length * 3 + 3) / 4];
        if (!Convert.TryFromBase64Chars(encoded, buffer, out var bytesWritten))
        {
            throw new ModValidationException("InvalidPublicKey", "Public key payload is not valid base64.");
        }

        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportSubjectPublicKeyInfo(buffer.AsSpan(0, bytesWritten), out var bytesRead);
            if (bytesRead != bytesWritten || !IsNistP256(ecdsa))
            {
                throw new ModValidationException("InvalidPublicKey", "Public key must encode a valid ECDSA P-256 SubjectPublicKeyInfo.");
            }

            return ecdsa;
        }
        catch (Exception exception) when (exception is not ModValidationException)
        {
            ecdsa.Dispose();
            throw new ModValidationException("InvalidPublicKey", $"Failed to parse ECDSA P-256 public key: {exception.Message}");
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    private static bool IsNistP256(ECDsa ecdsa)
    {
        if (ecdsa.KeySize != 256) return false;

        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var curve = parameters.Curve;
        if (!curve.IsNamed) return false;

        if (string.Equals(curve.Oid?.Value, NistP256Oid, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(curve.Oid?.FriendlyName, "nistP256", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(curve.Oid?.FriendlyName, "ECDSA_P256", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryDecodeSignature(string signature, out byte[] signatureBytes)
    {
        signatureBytes = [];
        if (string.IsNullOrEmpty(signature) || !signature.StartsWith(AlgorithmPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var encoded = signature.AsSpan(AlgorithmPrefix.Length);
        Span<byte> buffer = stackalloc byte[P256SignatureLength + 3];
        if (!Convert.TryFromBase64Chars(encoded, buffer, out var bytesWritten) || bytesWritten != P256SignatureLength)
        {
            return false;
        }

        signatureBytes = [.. buffer[..P256SignatureLength]];
        return true;
    }
}
