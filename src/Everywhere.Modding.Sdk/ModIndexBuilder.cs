using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Everywhere.Common;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Sdk;

/// <summary>
/// Builds and optionally signs static format-1 repository indexes from packaged mod archives.
/// </summary>
internal static class ModIndexBuilder
{
    private const int DirectoryInlineIconThresholdBytes = 16 * 1024;

    private static readonly SdkJsonSerializerContext SpecJsonContext = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        });

    /// <summary>
    /// Generates an ECDSA P-256 signing key pair and writes the private PEM and public key files.
    /// </summary>
    public static KeyPairGenerationResult GenerateKeyPair(string privateKeyPath, string publicKeyPath)
    {
        var (privateKeyPem, publicKey) = ModSignature.GenerateKeyPair();
        WriteTextAtomically(privateKeyPath, privateKeyPem);
        WriteTextAtomically(publicKeyPath, publicKey);
        return new KeyPairGenerationResult { PublicKey = publicKey };
    }

    /// <summary>
    /// Builds a validated <see cref="ModIndex"/> and associated static assets from an input build specification JSON file.
    /// </summary>
    public static IndexCreationResult Build(string specPath, string outputIndexPath, string? privateKeyPath)
    {
        var fullSpecPath = Path.GetFullPath(specPath);
        if (!File.Exists(fullSpecPath))
        {
            throw new ModValidationException("MissingIndexSpec", $"Index build specification '{fullSpecPath}' does not exist.");
        }

        ModIndexBuildSpec? spec;
        using (var stream = File.OpenRead(fullSpecPath))
        {
            spec = JsonSerializer.Deserialize(stream, SpecJsonContext.ModIndexBuildSpec);
        }

        if (spec is null || spec.Format != 1)
        {
            throw new ModValidationException("InvalidIndexSpec", "Index build specification is invalid or uses an unsupported format.");
        }

        if (spec.Layout is not ("directory" or "release"))
        {
            throw new ModValidationException("InvalidIndexSpec", $"Unsupported layout '{spec.Layout}'. Expected 'directory' or 'release'.");
        }

        string? privateKeyPem = null;
        string? publicKey = null;
        if (!string.IsNullOrWhiteSpace(privateKeyPath))
        {
            var fullKeyPath = Path.GetFullPath(privateKeyPath);
            if (!File.Exists(fullKeyPath))
            {
                throw new ModValidationException("MissingPrivateKey", $"Signing private key '{fullKeyPath}' does not exist.");
            }

            privateKeyPem = File.ReadAllText(fullKeyPath);
            publicKey = ModSignature.GetPublicKey(privateKeyPem);
        }

        var specDirectory = Path.GetDirectoryName(fullSpecPath) ?? ".";
        var fullOutputPath = Path.GetFullPath(outputIndexPath);
        var outputDirectory = Path.GetDirectoryName(fullOutputPath) ?? ".";
        Directory.CreateDirectory(outputDirectory);

        var inspected = new List<InspectedPackage>(spec.Packages.Length);
        var seenVersions = new HashSet<(string ModId, string NormalizedVersion)>();

        foreach (var packageSpec in spec.Packages)
        {
            var packagePath = Path.IsPathRooted(packageSpec.Path)
                ? Path.GetFullPath(packageSpec.Path)
                : Path.GetFullPath(Path.Combine(specDirectory, packageSpec.Path));
            var item = InspectPackage(packagePath, packageSpec);
            var key = (item.Manifest.Id, item.NormalizedVersion);
            if (!seenVersions.Add(key))
            {
                throw new ModValidationException(
                    "DuplicateModVersion",
                    $"Duplicate mod version '{item.Manifest.Id}@{item.Manifest.Version}' in index build specification.");
            }

            inspected.Add(item);
        }

        var entries = new List<ModIndexEntry>();
        foreach (var group in inspected.GroupBy(item => item.Manifest.Id, StringComparer.Ordinal))
        {
            var ordered = group
                .OrderByDescending(item => item.ParsedVersion)
                .ToArray();
            var latest = ordered[0];
            var versionEntries = new List<ModIndexVersion>(ordered.Length);

            foreach (var item in ordered)
            {
                versionEntries.Add(BuildVersionEntry(item, spec.Layout, outputDirectory, privateKeyPem));
            }

            var channels = BuildChannels(latest.Manifest.Id, ordered);
            var icon = ResolveModIcon(latest, spec.Layout, outputDirectory);
            var latestManifest = latest.Manifest;

            entries.Add(
                new ModIndexEntry
                {
                    Id = latestManifest.Id,
                    Name = new Dictionary<string, string>(latestManifest.Name, StringComparer.OrdinalIgnoreCase),
                    Description = new Dictionary<string, string>(latestManifest.Description, StringComparer.OrdinalIgnoreCase),
                    Author = latestManifest.Author,
                    Icon = icon,
                    Homepage = latestManifest.Homepage,
                    Repository = latestManifest.Repository,
                    License = latestManifest.License is null
                        ? null
                        : new ModIndexLicense
                        {
                            Spdx = latestManifest.License.Spdx,
                            IsAcceptanceRequired = latestManifest.License.IsAcceptanceRequired
                        },
                    Metadata = new ModIndexItemMetadata
                    {
                        Category = latest.Spec.Category,
                        Labels = [.. latest.Spec.Labels],
                        Keywords = [.. latest.Spec.Keywords],
                        SortValues = new Dictionary<string, double?>(latest.Spec.SortValues, StringComparer.Ordinal)
                    },
                    IsDefaultEnabled = latest.Spec.IsDefaultEnabled,
                    Channels = channels,
                    Versions = [.. versionEntries]
                });
        }

        var unsigned = new ModIndex
        {
            Format = 1,
            Id = spec.Id,
            Title = new Dictionary<string, string>(spec.Title, StringComparer.OrdinalIgnoreCase),
            Description = new Dictionary<string, string>(spec.Description, StringComparer.OrdinalIgnoreCase),
            GeneratedAt = spec.GeneratedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            PublicKey = publicKey,
            Mirrors = [.. spec.Mirrors],
            Metadata = spec.Metadata,
            Mods = [.. entries]
        };

        var finalIndex = privateKeyPem is null ? unsigned : unsigned with { Signature = ModSignature.SignIndex(unsigned, privateKeyPem) };

        ModIndexFile.Write(fullOutputPath, finalIndex);
        return new IndexCreationResult { Path = fullOutputPath, ModCount = entries.Count };
    }

    private static InspectedPackage InspectPackage(string packagePath, ModIndexPackageSpec packageSpec)
    {
        if (!File.Exists(packagePath))
        {
            throw new ModValidationException("MissingPackageArchive", $"Package archive '{packagePath}' does not exist.");
        }

        long size;
        string sha256;
        using (var stream = File.OpenRead(packagePath))
        {
            size = stream.Length;
            sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
        }

        using var archive = ZipFile.OpenRead(packagePath);
        var manifestEntry = archive.GetEntry(ModManifestFile.FileName)
            ?? throw new ModValidationException("MissingManifest", $"Package archive '{packagePath}' does not contain '{ModManifestFile.FileName}'.");
        ModManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = ModManifestFile.Read(manifestStream);
        }

        ValidateArchiveAssets(archive, manifest, packagePath);

        var parsedVersion = ModCompatibility.ParseVersion(manifest.Version);
        var publishedAt = packageSpec.PublishedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(packagePath)).ToUnixTimeSeconds();
        return new InspectedPackage(packagePath, packageSpec, manifest, parsedVersion, parsedVersion.ToNormalizedString(), size, sha256, publishedAt);
    }

    private static void ValidateArchiveAssets(ZipArchive archive, ModManifest manifest, string packagePath)
    {
        var files = manifest.Entry switch
        {
            UniversalModEntry universal => universal.Assemblies,
            PlatformModEntry platform => platform.Variants.SelectMany(variant => variant.Assemblies),
            _ => []
        };

        var metadataFiles = manifest.Readme.Values.AsEnumerable();
        if (manifest.Icon is not null) metadataFiles = metadataFiles.Append(manifest.Icon);
        if (manifest.License is { } license)
        {
            if (license.File is not null) metadataFiles = metadataFiles.Append(license.File);
            metadataFiles = metadataFiles.Concat(license.ThirdPartyFiles);
        }

        foreach (var file in files.Concat(metadataFiles))
        {
            if (archive.GetEntry(file) is null)
            {
                throw new ModValidationException("MissingAsset", $"Package archive '{packagePath}' is missing declared asset '{file}'.");
            }
        }
    }

    private static ModIndexVersion BuildVersionEntry(
        InspectedPackage item,
        string layout,
        string outputDirectory,
        string? privateKeyPem
    )
    {
        var manifest = item.Manifest;
        string artifactUrl;
        var readmeUrls = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? licenseUrl = null;
        string[] thirdPartyLicenseUrls = [];

        if (layout == "directory")
        {
            var defaultRelativeArtifact = $"packages/{manifest.Id}/{item.NormalizedVersion}/{manifest.Id}-{item.NormalizedVersion}.zip";
            artifactUrl = item.Spec.ArtifactUrl ?? defaultRelativeArtifact;
            if (item.Spec.ArtifactUrl is null)
            {
                CopyFileToOutput(item.PackagePath, outputDirectory, defaultRelativeArtifact);
            }

            using var archive = ZipFile.OpenRead(item.PackagePath);
            var detailPrefix = $"details/{manifest.Id}/{item.NormalizedVersion}";

            foreach (var (language, relativePath) in manifest.Readme)
            {
                var targetRelative = $"{detailPrefix}/{relativePath}";
                ExtractEntryToOutput(archive, relativePath, outputDirectory, targetRelative);
                readmeUrls.Add(language, targetRelative);
            }

            if (manifest.License is { } license)
            {
                if (license.File is not null)
                {
                    var targetRelative = $"{detailPrefix}/{license.File}";
                    ExtractEntryToOutput(archive, license.File, outputDirectory, targetRelative);
                    licenseUrl = targetRelative;
                }

                if (license.ThirdPartyFiles.Length > 0)
                {
                    var urls = new List<string>(license.ThirdPartyFiles.Length);
                    foreach (var noticePath in license.ThirdPartyFiles)
                    {
                        var targetRelative = $"{detailPrefix}/{noticePath}";
                        ExtractEntryToOutput(archive, noticePath, outputDirectory, targetRelative);
                        urls.Add(targetRelative);
                    }

                    thirdPartyLicenseUrls = [.. urls];
                }
            }
        }
        else
        {
            var tag = item.Spec.ReleaseTag ?? $"{manifest.Id}-{item.NormalizedVersion}";
            ValidateReleaseTag(tag);
            artifactUrl = item.Spec.ArtifactUrl ?? $"../{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(Path.GetFileName(item.PackagePath))}";
        }

        var signature = privateKeyPem is null
            ? null
            : ModSignature.SignArtifact(manifest.Id, manifest.Version, item.Sha256, item.Size, privateKeyPem);

        return new ModIndexVersion
        {
            Version = manifest.Version,
            PublishedAt = item.PublishedAt,
            Readme = readmeUrls,
            LicenseUrl = licenseUrl,
            ThirdPartyLicenseUrls = thirdPartyLicenseUrls,
            Entry = manifest.Entry,
            Artifact = new ModArtifact
            {
                Url = artifactUrl,
                Size = item.Size,
                Sha256 = item.Sha256,
                Signature = signature
            }
        };
    }

    private static Dictionary<string, string> BuildChannels(string modId, InspectedPackage[] orderedDescending)
    {
        var channels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in orderedDescending)
        {
            foreach (var channel in item.Spec.Channels)
            {
                if (!channels.TryAdd(channel, item.Manifest.Version))
                {
                    throw new ModValidationException(
                        "InvalidChannel",
                        $"Channel '{channel}' is assigned to multiple versions of '{modId}'.");
                }
            }
        }

        if (channels.Count != 0)
        {
            return channels;
        }

        var latestStable = orderedDescending.FirstOrDefault(item => !item.ParsedVersion.IsPrerelease);
        if (latestStable is not null)
        {
            channels["stable"] = latestStable.Manifest.Version;
        }

        return channels;
    }

    private static string? ResolveModIcon(InspectedPackage latest, string layout, string outputDirectory)
    {
        var iconPath = latest.Manifest.Icon;
        if (iconPath is null) return null;

        using var archive = ZipFile.OpenRead(latest.PackagePath);
        var entry = archive.GetEntry(iconPath)
            ?? throw new ModValidationException("MissingIcon", $"Package '{latest.Manifest.Id}' is missing declared icon '{iconPath}'.");

        if (entry.Length > ModIndexFile.MaxInlineIconBytes && layout == "release")
        {
            throw new ModValidationException(
                "InvalidIcon",
                $"Icon '{iconPath}' exceeds {ModIndexFile.MaxInlineIconBytes} bytes required for inline release-layout icons.");
        }

        using var memory = new MemoryStream();
        using (var entryStream = entry.Open())
        {
            entryStream.CopyTo(memory);
        }

        var bytes = memory.ToArray();
        var mimeType = GetIconMimeType(iconPath);

        if (layout == "release" || bytes.Length <= DirectoryInlineIconThresholdBytes)
        {
            if (bytes.Length > ModIndexFile.MaxInlineIconBytes)
            {
                throw new ModValidationException("InvalidIcon", $"Inline icon '{iconPath}' exceeds {ModIndexFile.MaxInlineIconBytes} bytes.");
            }

            return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }

        var extension = Path.GetExtension(iconPath).ToLowerInvariant();
        var targetRelative = $"icons/{latest.Manifest.Id}{extension}";
        var destination = PackagePath.Resolve(outputDirectory, targetRelative);
        WriteBytesAtomically(destination, bytes);
        return targetRelative;
    }

    private static string GetIconMimeType(string iconPath) =>
        Path.GetExtension(iconPath).ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => throw new ModValidationException("InvalidIcon", $"Unsupported icon extension for '{iconPath}'.")
        };

    private static void ValidateReleaseTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag) || tag is "." or ".." || tag.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ModValidationException("InvalidIndexSpec", $"Invalid release tag '{tag}'.");
        }
    }

    private static void CopyFileToOutput(string sourcePath, string outputDirectory, string relativeTarget)
    {
        var destination = PackagePath.Resolve(outputDirectory, relativeTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? outputDirectory);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.Copy(sourcePath, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void ExtractEntryToOutput(ZipArchive archive, string archivePath, string outputDirectory, string relativeTarget)
    {
        var entry = archive.GetEntry(archivePath)
            ?? throw new ModValidationException("MissingAsset", $"Archive does not contain '{archivePath}'.");
        var destination = PackagePath.Resolve(outputDirectory, relativeTarget);
        Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? outputDirectory);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var input = entry.Open())
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
            }

            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteTextAtomically(string path, string content)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllText(temporary, content);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? ".");
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private sealed record InspectedPackage(
        string PackagePath,
        ModIndexPackageSpec Spec,
        ModManifest Manifest,
        SemanticVersion ParsedVersion,
        string NormalizedVersion,
        long Size,
        string Sha256,
        long PublishedAt
    );
}

/// <summary>
/// Describes the input specification consumed by <c>Everywhere.Modding.Sdk build-index</c>.
/// </summary>
internal sealed record ModIndexBuildSpec
{
    public int Format { get; set; } = 1;

    public required string Id { get; init; }

    public Dictionary<string, string> Title { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Description { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public long? GeneratedAt { get; init; }

    public string[] Mirrors { get; set; } = [];

    public ModIndexMetadata Metadata { get; set; } = new();

    public string Layout { get; set; } = "directory";

    public required ModIndexPackageSpec[] Packages { get; init; }
}

/// <summary>
/// Describes one packaged mod archive included in an index build specification.
/// </summary>
internal sealed record ModIndexPackageSpec
{
    public required string Path { get; init; }

    public long? PublishedAt { get; init; }

    public string? ReleaseTag { get; init; }

    public string? ArtifactUrl { get; init; }

    public string? Category { get; init; }

    public string[] Labels { get; set; } = [];

    public string[] Keywords { get; set; } = [];

    public Dictionary<string, double?> SortValues { get; set; } = new(StringComparer.Ordinal);

    public bool IsDefaultEnabled { get; set; }

    public string[] Channels { get; set; } = [];
}
