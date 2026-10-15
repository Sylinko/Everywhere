using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting.Installation;

/// <summary>
/// Imports user-managed ZIPs and folders at startup and reuses unchanged runtime installations.
/// The source directory determines membership; the index only remembers completed imports.
/// Call before activating any plugins, never concurrently with another catalog operation.
/// </summary>
public sealed partial class PluginSourceCatalog(string sourceDirectory, PluginPackageStore store)
{
    /// <summary>
    /// Imports the complete source set. Removed sources are not selected, and duplicate plugin IDs are rejected.
    /// Old installations remain available for deferred cleanup instead of deleting potentially retained files.
    /// </summary>
    public async Task<IReadOnlyList<PluginInstallation>> ImportAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(store.RootDirectory);

        var indexPath = Path.Combine(store.RootDirectory, "sources.json");
        var previous = File.Exists(indexPath) ?
            JsonSerializer.Deserialize(
                await File.ReadAllTextAsync(indexPath, cancellationToken),
                SourceIndexJsonSerializerContext.Default.SourceIndex) ?? [] :
            new Dictionary<string, ImportRecord>();
        var next = new Dictionary<string, ImportRecord>();
        var installations = new List<PluginInstallation>();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in Directory.EnumerateFileSystemEntries(sourceDirectory).Order(StringComparer.Ordinal))
        {
            var isDirectory = Directory.Exists(source);

            if (isDirectory ?
                    !File.Exists(Path.Combine(source, PluginManifestFile.FileName)) :
                    !string.Equals(Path.GetExtension(source), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var key = Path.GetFileName(source);
            var fingerprint = await FingerprintAsync(source, isDirectory, cancellationToken);
            PluginInstallation installation;

            if (previous.TryGetValue(key, out var record) && record.Fingerprint == fingerprint &&
                File.Exists(Path.Combine(PackagePath.Resolve(store.RootDirectory, record.RelativeDirectory), PluginManifestFile.FileName)))
            {
                var directory = PackagePath.Resolve(store.RootDirectory, record.RelativeDirectory);
                installation = new PluginInstallation
                {
                    InstallationId = record.InstallationId,
                    Directory = directory,
                    Manifest = PluginManifestFile.Read(directory)
                };
            }
            else
            {
                if (isDirectory)
                {
                    installation = await store.ImportDirectoryAsync(source, cancellationToken);
                }
                else
                {
                    await using var stream = File.OpenRead(source);
                    installation = await store.ImportAsync(stream, cancellationToken);
                }

                record = new ImportRecord
                {
                    Fingerprint = fingerprint,
                    InstallationId = installation.InstallationId,
                    RelativeDirectory = Path.GetRelativePath(store.RootDirectory, installation.Directory).Replace('\\', '/')
                };
            }

            if (!ids.Add(installation.Manifest.Id))
            {
                throw new PluginValidationException(
                    "DuplicatePlugin",
                    $"More than one source provides '{installation.Manifest.Id}'. Keep one ZIP or folder per plugin.");
            }

            installations.Add(installation);
            next.Add(key, record);
        }

        // Publish only after the complete source set is imported. This is import state, not activation success.
        Directory.CreateDirectory(store.StagingDirectory);
        var temporary = Path.Combine(store.StagingDirectory, Guid.NewGuid().ToString("N") + ".json");

        try
        {
            await File.WriteAllTextAsync(
                temporary,
                JsonSerializer.Serialize(next, SourceIndexJsonSerializerContext.Default.SourceIndex),
                cancellationToken);
            File.Move(temporary, indexPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return installations;
    }

    /// <summary>
    /// Detects source changes, including same-version rebuilds, without relying on timestamps or language folders.
    /// This is cache invalidation, not a package authenticity check.
    /// </summary>
    private static async Task<string> FingerprintAsync(string source, bool isDirectory, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var files = isDirectory ? PluginPackageStore.EnumerateSourceFiles(source) : [source];

        foreach (var file in files)
        {
            var name = isDirectory ? Path.GetRelativePath(source, file).Replace('\\', '/') : Path.GetFileName(file);
            hash.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
            await using var stream = File.OpenRead(file);
            hash.AppendData(await SHA256.HashDataAsync(stream, cancellationToken));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    /// <summary>
    /// Remembers one completed source import using scalar paths and content identity.
    /// </summary>
    private sealed record ImportRecord
    {
        /// <summary>
        /// Gets the source content identity used for reuse.
        /// </summary>
        public required string Fingerprint { get; init; }

        /// <summary>
        /// Gets the immutable runtime installation identity.
        /// </summary>
        public required string InstallationId { get; init; }

        /// <summary>
        /// Gets the package-store-relative runtime path.
        /// </summary>
        public required string RelativeDirectory { get; init; }
    }

    /// <summary>
    /// Generates metadata for the private import index while retaining its default PascalCase JSON format.
    /// </summary>
    [JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(Dictionary<string, ImportRecord>), TypeInfoPropertyName = "SourceIndex")]
    private partial class SourceIndexJsonSerializerContext : JsonSerializerContext;
}