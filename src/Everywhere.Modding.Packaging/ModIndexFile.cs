using System.Buffers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Reads, writes, resolves, and validates static format-1 mod repository indexes.
/// </summary>
public static partial class ModIndexFile
{
    /// <summary>
    /// Gets the canonical filename of a repository index document.
    /// </summary>
    public const string FileName = "index.json";

    /// <summary>
    /// Gets the maximum decoded payload size in bytes allowed for inline <c>data:</c> icons.
    /// </summary>
    public const int MaxInlineIconBytes = 32 * 1024;

    private static readonly byte[] SigningDomainHeader = [.. "everywhere-mod-index-v1\n"u8];
    private static readonly Uri ProbeBaseUri = new("https://index.invalid/a/b/c/index.json", UriKind.Absolute);

    private static readonly PackagingJsonSerializerContext JsonContext = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        });

    private static readonly PackagingJsonSerializerContext CanonicalJsonContext = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = false,
            RespectNullableAnnotations = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

    /// <summary>
    /// Reads and validates an index file from disk.
    /// </summary>
    public static ModIndex Read(string path, Uri? indexUri = null)
    {
        using var stream = File.OpenRead(path);
        return Read(stream, indexUri);
    }

    /// <summary>
    /// Reads and validates an index document synchronously from a stream.
    /// </summary>
    public static ModIndex Read(Stream stream, Uri? indexUri = null)
    {
        var index = JsonSerializer.Deserialize(stream, JsonContext.ModIndex);
        if (index is null)
        {
            throw new ModValidationException("MissingIndex", "The repository index document is empty.");
        }

        Validate(index, indexUri);
        return index;
    }

    /// <summary>
    /// Reads and validates an index document asynchronously from a stream.
    /// </summary>
    public static async ValueTask<ModIndex> ReadAsync(Stream stream, Uri? indexUri = null, CancellationToken cancellationToken = default)
    {
        var index = await JsonSerializer.DeserializeAsync(stream, JsonContext.ModIndex, cancellationToken).ConfigureAwait(false);
        if (index is null)
        {
            throw new ModValidationException("MissingIndex", "The repository index document is empty.");
        }

        Validate(index, indexUri);
        return index;
    }

    /// <summary>
    /// Validates and writes an index document to a file path atomically via a temporary sibling file.
    /// </summary>
    public static void Write(string path, ModIndex index)
    {
        Validate(index);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath) ?? ".";
        Directory.CreateDirectory(directory);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, index, JsonContext.ModIndex);
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    /// <summary>
    /// Validates and writes an index document to a stream.
    /// </summary>
    public static void Write(Stream stream, ModIndex index)
    {
        Validate(index);
        JsonSerializer.Serialize(stream, index, JsonContext.ModIndex);
    }

    /// <summary>
    /// Validates schema version, metadata declarations, asset references, entry rules, and signatures.
    /// </summary>
    public static void Validate(ModIndex index, Uri? indexUri = null)
    {
        if (indexUri is not null)
        {
            ValidateBaseIndexUri(indexUri);
        }

        if (index.Format != 1 || !ModValidationRules.IsValidIdentifier(index.Id))
        {
            throw new ModValidationException("InvalidIndex", "Unsupported format version or invalid repository index ID.");
        }

        if (index.Title.Count == 0)
        {
            throw new ModValidationException("InvalidIndex", "Repository index must define at least one localized title.");
        }

        ModValidationRules.ValidateTranslations(index.Title);
        ModValidationRules.ValidateTranslations(index.Description);

        if (index.GeneratedAt < 0)
        {
            throw new ModValidationException("InvalidIndex", "Generated timestamp cannot be negative.");
        }

        ValidateMirrors(index.Mirrors);
        ValidateMetadata(index.Metadata);

        var modIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in index.Mods)
        {
            if (!modIds.Add(entry.Id))
            {
                throw new ModValidationException("DuplicateMod", $"Duplicate mod ID '{entry.Id}' in index '{index.Id}'.");
            }

            ValidateModEntry(entry, index.Metadata, indexUri);
        }

        ValidateSignatures(index);
    }

    /// <summary>
    /// Resolves a relative or absolute asset reference against the base URL from which the index was fetched.
    /// </summary>
    public static Uri ResolveAssetUri(Uri indexUri, string assetReference)
    {
        ValidateBaseIndexUri(indexUri);

        if (string.IsNullOrWhiteSpace(assetReference) ||
            assetReference.StartsWith("//", StringComparison.Ordinal) ||
            assetReference.Contains('\\'))
        {
            throw new ModValidationException("InvalidUrl", $"Invalid asset reference '{assetReference}'.");
        }

        if (!Uri.TryCreate(assetReference, UriKind.Absolute, out var resolved) &&
            !Uri.TryCreate(indexUri, assetReference, out resolved))
        {
            throw new ModValidationException("InvalidUrl", $"Cannot resolve asset reference '{assetReference}' against '{indexUri}'.");
        }

        if (!string.IsNullOrEmpty(resolved.UserInfo))
        {
            throw new ModValidationException("InvalidUrl", $"Credentials are disallowed in asset URL '{assetReference}'.");
        }

        var isAllowed = indexUri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" => IsHttpScheme(resolved),
            "file" => IsHttpScheme(resolved) ||
                (string.Equals(resolved.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(resolved.Host, indexUri.Host, StringComparison.OrdinalIgnoreCase)),
            _ => false
        };

        if (!isAllowed)
        {
            throw new ModValidationException(
                "InvalidUrl",
                $"Asset reference '{assetReference}' resolves to disallowed scheme '{resolved.Scheme}' for '{indexUri.Scheme}' index.");
        }

        return resolved;
    }

    /// <summary>
    /// Checks whether a string is a 64-character lowercase hexadecimal SHA-256 digest.
    /// </summary>
    internal static bool IsValidSha256Hex(string value) => !string.IsNullOrEmpty(value) && Sha256HexPattern().IsMatch(value);

    /// <summary>
    /// Builds deterministic UTF-8 canonical bytes signed by <see cref="ModSignature.SignIndex"/> and verified by <see cref="ModSignature.VerifyIndex"/>.
    /// </summary>
    internal static byte[] CreateSigningPayload(ModIndex index)
    {
        var unsigned = index with { Signature = null };
        var node = CanonicalizeNode(JsonSerializer.SerializeToNode(unsigned, CanonicalJsonContext.ModIndex))
            ?? throw new InvalidOperationException("Failed to serialize canonical index payload.");
        var buffer = new ArrayBufferWriter<byte>();
        buffer.Write(SigningDomainHeader);

        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            node.WriteTo(writer);
            writer.Flush();
        }

        return [.. buffer.WrittenSpan];
    }

    private static void ValidateMirrors(string[] mirrors)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mirror in mirrors)
        {
            var uri = ModValidationRules.ValidateHttpUri(mirror);
            if (!seen.Add(uri.AbsoluteUri))
            {
                throw new ModValidationException("InvalidIndex", $"Duplicate mirror URL '{mirror}'.");
            }
        }
    }

    private static void ValidateMetadata(ModIndexMetadata metadata)
    {
        foreach (var (id, declaration) in metadata.Categories)
        {
            if (string.IsNullOrWhiteSpace(id) || declaration.Title.Count == 0)
            {
                throw new ModValidationException("InvalidIndexMetadata", $"Invalid category declaration '{id}'.");
            }

            ModValidationRules.ValidateTranslations(declaration.Title);
        }

        foreach (var (id, declaration) in metadata.Labels)
        {
            if (string.IsNullOrWhiteSpace(id) || declaration.Title.Count == 0)
            {
                throw new ModValidationException("InvalidIndexMetadata", $"Invalid label declaration '{id}'.");
            }

            ModValidationRules.ValidateTranslations(declaration.Title);
        }

        foreach (var (id, declaration) in metadata.SortKeys)
        {
            if (string.IsNullOrWhiteSpace(id) ||
                declaration.Title.Count == 0 ||
                declaration.Order is not ("asc" or "desc"))
            {
                throw new ModValidationException("InvalidIndexMetadata", $"Invalid sort key declaration '{id}'.");
            }

            ModValidationRules.ValidateTranslations(declaration.Title);
        }
    }

    private static void ValidateModEntry(ModIndexEntry entry, ModIndexMetadata indexMetadata, Uri? indexUri)
    {
        if (!ModValidationRules.IsValidIdentifier(entry.Id))
        {
            throw new ModValidationException("InvalidMod", $"Invalid mod ID '{entry.Id}'.");
        }

        ModValidationRules.ValidateTranslations(entry.Name);
        ModValidationRules.ValidateTranslations(entry.Description);
        ModValidationRules.ValidateAuthor(entry.Author);

        if (entry.Icon is not null)
        {
            ValidateIconReference(entry.Icon, indexUri);
        }

        if (entry.Homepage is not null)
        {
            ModValidationRules.ValidateHttpUri(entry.Homepage);
        }

        ModValidationRules.ValidateRepository(entry.Repository);

        if (entry.License is not null)
        {
            ModValidationRules.ValidateSpdx(entry.License.Spdx);
        }

        ValidateItemMetadata(entry.Id, entry.Metadata, indexMetadata);

        if (entry.Versions.Length == 0)
        {
            throw new ModValidationException("InvalidModVersion", $"Mod '{entry.Id}' must declare at least one version.");
        }

        var listedVersions = new HashSet<string>(StringComparer.Ordinal);
        var normalizedVersions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var version in entry.Versions)
        {
            var parsedVersion = ModCompatibility.ParseVersion(version.Version);
            if (!listedVersions.Add(version.Version) || !normalizedVersions.Add(parsedVersion.ToNormalizedString()))
            {
                throw new ModValidationException("InvalidModVersion", $"Duplicate version '{version.Version}' in mod '{entry.Id}'.");
            }

            ValidateVersionEntry(entry.Id, version, indexUri);
        }

        foreach (var (channel, targetVersion) in entry.Channels)
        {
            if (string.IsNullOrWhiteSpace(channel))
            {
                throw new ModValidationException("InvalidChannel", $"Channel name in mod '{entry.Id}' cannot be blank.");
            }

            if (!listedVersions.Contains(targetVersion))
            {
                throw new ModValidationException(
                    "InvalidChannel",
                    $"Channel '{channel}' in mod '{entry.Id}' targets unlisted version '{targetVersion}'.");
            }
        }
    }

    private static void ValidateItemMetadata(string modId, ModIndexItemMetadata metadata, ModIndexMetadata indexMetadata)
    {
        if (metadata.Category is not null && !indexMetadata.Categories.ContainsKey(metadata.Category))
        {
            throw new ModValidationException(
                "UndeclaredIndexMetadata",
                $"Mod '{modId}' references undeclared category '{metadata.Category}'.");
        }

        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var label in metadata.Labels)
        {
            if (!indexMetadata.Labels.ContainsKey(label))
            {
                throw new ModValidationException("UndeclaredIndexMetadata", $"Mod '{modId}' references undeclared label '{label}'.");
            }

            if (!labels.Add(label))
            {
                throw new ModValidationException("InvalidIndexMetadata", $"Mod '{modId}' repeats label '{label}'.");
            }
        }

        foreach (var (sortKey, value) in metadata.SortValues)
        {
            if (!indexMetadata.SortKeys.ContainsKey(sortKey))
            {
                throw new ModValidationException("UndeclaredIndexMetadata", $"Mod '{modId}' references undeclared sort key '{sortKey}'.");
            }

            if (value is { } numeric && !double.IsFinite(numeric))
            {
                throw new ModValidationException(
                    "InvalidIndexMetadata",
                    $"Sort value '{sortKey}' for mod '{modId}' must be finite.");
            }
        }
    }

    private static void ValidateVersionEntry(string modId, ModIndexVersion version, Uri? indexUri)
    {
        if (version.PublishedAt < 0)
        {
            throw new ModValidationException("InvalidModVersion", $"Publish timestamp for '{modId}@{version.Version}' cannot be negative.");
        }

        ModValidationRules.ValidateTranslations(version.Readme);

        foreach (var readmeUrl in version.Readme.Values)
        {
            ValidateAssetReference(readmeUrl, indexUri);
        }

        if (version.LicenseUrl is not null)
        {
            ValidateAssetReference(version.LicenseUrl, indexUri);
        }

        foreach (var thirdPartyUrl in version.ThirdPartyLicenseUrls)
        {
            ValidateAssetReference(thirdPartyUrl, indexUri);
        }

        ModManifestFile.ValidateEntryDeclaration(version.Entry, modId);

        ValidateAssetReference(version.Artifact.Url, indexUri);
        if (version.Artifact.Size <= 0)
        {
            throw new ModValidationException("InvalidArtifact", $"Artifact size for '{modId}@{version.Version}' must be positive.");
        }

        if (!IsValidSha256Hex(version.Artifact.Sha256))
        {
            throw new ModValidationException(
                "InvalidArtifact",
                $"Artifact SHA-256 digest for '{modId}@{version.Version}' must be 64 lowercase hex characters.");
        }
    }

    private static void ValidateSignatures(ModIndex index)
    {
        if (index.PublicKey is null)
        {
            if (index.Signature is not null)
            {
                throw new ModValidationException("InvalidSignature", "Index signature requires a declared publicKey.");
            }

            foreach (var entry in index.Mods)
            {
                foreach (var version in entry.Versions)
                {
                    if (version.Artifact.Signature is not null)
                    {
                        throw new ModValidationException(
                            "InvalidSignature",
                            $"Artifact signature for '{entry.Id}@{version.Version}' requires an index publicKey.");
                    }
                }
            }

            return;
        }

        ModSignature.ValidatePublicKey(index.PublicKey);

        if (string.IsNullOrWhiteSpace(index.Signature) || !ModSignature.VerifyIndex(index, index.PublicKey))
        {
            throw new ModValidationException("InvalidSignature", $"Index '{index.Id}' failed signature verification.");
        }

        foreach (var entry in index.Mods)
        {
            foreach (var version in entry.Versions)
            {
                var artifactSignature = version.Artifact.Signature;
                if (string.IsNullOrWhiteSpace(artifactSignature) ||
                    !ModSignature.VerifyArtifact(
                        entry.Id,
                        version.Version,
                        version.Artifact.Sha256,
                        version.Artifact.Size,
                        artifactSignature,
                        index.PublicKey))
                {
                    throw new ModValidationException(
                        "InvalidSignature",
                        $"Artifact '{entry.Id}@{version.Version}' failed signature verification.");
                }
            }
        }
    }

    private static void ValidateIconReference(string icon, Uri? indexUri)
    {
        var dataMatch = InlineDataIconPattern().Match(icon);
        if (dataMatch.Success)
        {
            var payload = dataMatch.Groups[1].Value;
            var decodedBuffer = new byte[(payload.Length * 3 + 3) / 4];
            if (!Convert.TryFromBase64String(payload, decodedBuffer, out var decodedLength) || decodedLength > MaxInlineIconBytes)
            {
                throw new ModValidationException("InvalidIcon", $"Inline data icon exceeds {MaxInlineIconBytes} bytes or has invalid base64.");
            }

            return;
        }

        if (icon.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            throw new ModValidationException("InvalidIcon", "Unsupported inline data icon MIME type or encoding.");
        }

        ValidateAssetReference(icon, indexUri);
    }

    private static void ValidateAssetReference(string assetReference, Uri? indexUri)
    {
        ResolveAssetUri(indexUri ?? ProbeBaseUri, assetReference);
    }

    private static void ValidateBaseIndexUri(Uri indexUri)
    {
        if (!indexUri.IsAbsoluteUri)
        {
            throw new ModValidationException("InvalidUrl", "Index URI must be absolute.");
        }

        var isAllowedScheme =
            IsHttpScheme(indexUri) ||
            string.Equals(indexUri.Scheme, Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase);

        if (!isAllowedScheme || !string.IsNullOrEmpty(indexUri.UserInfo))
        {
            throw new ModValidationException("InvalidUrl", $"Unsupported index URI '{indexUri}'.");
        }
    }

    private static bool IsHttpScheme(Uri uri) =>
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    private static JsonNode? CanonicalizeNode(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
            {
                var entries = obj.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
                obj.Clear();
                foreach (var (key, value) in entries)
                {
                    obj.Add(key, CanonicalizeNode(value));
                }

                return obj;
            }
            case JsonArray array:
            {
                for (var i = 0; i < array.Count; i++)
                {
                    var item = array[i];
                    array[i] = null;
                    array[i] = CanonicalizeNode(item);
                }

                return array;
            }
            default:
                return node;
        }
    }

    [GeneratedRegex("^data:(?:image/svg\\+xml|image/png|image/jpeg|image/webp);base64,([A-Za-z0-9+/]*={0,2})$", RegexOptions.CultureInvariant)]
    private static partial Regex InlineDataIconPattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256HexPattern();
}
