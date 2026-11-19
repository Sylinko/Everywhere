using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Packaging;

namespace Everywhere.Modding.Sdk;

/// <summary>
/// Packages explicit build outputs while excluding host-owned managed identities.
/// </summary>
internal static class ModPackager
{
    /// <summary>
    /// Creates a ZIP and generated manifest from resolved build inputs, never from a hand-maintained manifest.
    /// </summary>
    public static void Pack(
        string assemblyPath,
        string referenceList,
        string payloadList,
        string modId,
        string version,
        string entryType,
        string framework,
        string hostRange,
        string apiRange,
        string platform,
        string outputPath,
        string presentationDirectory,
        string projectDirectory)
    {
        var host = JsonSerializer.Deserialize(
            File.ReadAllText(referenceList + ".host.json"),
            SdkJsonSerializerContext.Default.ModHostDescriptor)
            ?? throw new ModValidationException("MissingHostImport", "The build has no host import record.");
        var hostAssemblies = File.ReadAllLines(referenceList + ".runtime.txt")
            .Select(AssemblyName.GetAssemblyName)
            .ToDictionary(GetIdentityKey, StringComparer.OrdinalIgnoreCase);
        var hostImplementationNames = File.ReadAllLines(referenceList)
            .Select(path => AssemblyName.GetAssemblyName(path).Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var managed = new List<string>();
        var native = new List<string>();
        var content = new List<string>();
        var hasHostImplementationReference = false;
        var contractName = typeof(IMod).Assembly.GetName().Name;

        foreach (var outputItem in File.ReadAllLines(payloadList)
                     .Append(assemblyPath + "|" + Path.GetFileName(assemblyPath) + "|runtime")
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parts = outputItem.Split('|');
            var path = parts[0];
            var name = parts[1].Replace('\\', '/');
            PackagePath.ValidateRelative(name);
            if (!File.Exists(path)) throw new ModValidationException("MissingBuildOutput", path);

            var assetType = parts[2];
            if (assetType is "runtime" or "resources")
            {
                // A malformed managed output is a build error, never a native-library fallback.
                var identity = AssemblyName.GetAssemblyName(path);
                if (hostAssemblies.TryGetValue(GetIdentityKey(identity), out var provided))
                {
                    if (provided.Version < identity.Version ||
                        !(provided.GetPublicKeyToken() ?? []).SequenceEqual(identity.GetPublicKeyToken() ?? []))
                    {
                        throw new ModValidationException(
                            "HostDependencyConflict",
                            $"Host '{provided}' cannot replace build dependency '{identity}'.");
                    }

                    continue;
                }

                managed.Add(name);

                using var stream = File.OpenRead(path);
                using var pe = new PEReader(stream);
                var metadata = pe.GetMetadataReader();

                foreach (var handle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetAssemblyReference(handle);
                    var referenceName = metadata.GetString(reference.Name);

                    if (referenceName != contractName && hostImplementationNames.Contains(referenceName))
                    {
                        hasHostImplementationReference = true;
                    }
                }
            }
            else if (assetType == "native" && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                native.Add(name);
            }
            else if (assetType is "content" or "native")
            {
                // NuGet also places helper executables in native assets; they are launched, not resolved as libraries.
                content.Add(name);
            }
            else
            {
                throw new ModValidationException("InvalidAssetType", $"Unsupported build asset type '{assetType}' for '{name}'.");
            }

            if (!files.TryAdd(name, path) && files[name] != path)
            {
                throw new ModValidationException("PackagePathConflict", $"Different build outputs map to '{name}'.");
            }
        }

        if (hasHostImplementationReference && string.IsNullOrWhiteSpace(hostRange))
        {
            throw new ModValidationException(
                "MissingHostRange",
                "Packaging requires EverywhereHostVersion; an exact range for the imported host is a valid starting point.");
        }

        if (!string.IsNullOrWhiteSpace(hostRange) && !ModCompatibility.IsSatisfied(hostRange, host.HostVersion))
        {
            throw new ModValidationException("BuildHostOutsideRange", "The declared host range excludes the imported build host.");
        }

        if (native.Count != 0 && string.IsNullOrEmpty(platform))
        {
            throw new ModValidationException("NativePlatformRequired", "Native assets require an explicit EverywhereModPlatform and matching RuntimeIdentifier.");
        }

        var entryAssembly = Path.GetFileName(assemblyPath);
        if (string.IsNullOrWhiteSpace(apiRange))
        {
            var apiVersion = ModCompatibility.ParseVersion(host.ModApiVersion);
            apiRange = apiVersion.IsPrerelease ?
                $"[{apiVersion.ToNormalizedString()}]" :
                $"[{apiVersion.ToNormalizedString()},{(long)apiVersion.Major + 1}.0.0-0)";
        }

        if (!ModCompatibility.IsSatisfied(apiRange, host.ModApiVersion))
        {
            throw new ModValidationException("BuildApiOutsideRange", "The declared Mod API range excludes the imported build contract.");
        }

        ModEntry entry = string.IsNullOrEmpty(platform) ?
            new UniversalModEntry
            {
                Assembly = entryAssembly,
                Type = entryType,
                Framework = framework,
                ModApiVersion = apiRange,
                HostVersion = string.IsNullOrWhiteSpace(hostRange) ? null : hostRange,
                Assemblies = [.. managed.Distinct(StringComparer.OrdinalIgnoreCase)],
                NativeLibraries = [.. native.Distinct(StringComparer.OrdinalIgnoreCase)],
                ContentFiles = [.. content.Distinct(StringComparer.OrdinalIgnoreCase)]
            } :
            new PlatformModEntry
            {
                Variants =
                [
                    new ModPlatformVariant
                    {
                        Platform = platform,
                        Assembly = entryAssembly,
                        Type = entryType,
                        Framework = framework,
                        ModApiVersion = apiRange,
                        HostVersion = string.IsNullOrWhiteSpace(hostRange) ? null : hostRange,
                        Assemblies = [.. managed.Distinct(StringComparer.OrdinalIgnoreCase)],
                        NativeLibraries = [.. native.Distinct(StringComparer.OrdinalIgnoreCase)],
                        ContentFiles = [.. content.Distinct(StringComparer.OrdinalIgnoreCase)]
                    }
                ]
            };
        var manifest = new ModManifest { Id = modId, Version = version, Entry = entry };
        ApplyPresentation(manifest, presentationDirectory, projectDirectory, files);
        ModManifestFile.Validate(manifest);
        var output = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(output) ?? ".";
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".mod-pack-" + Guid.NewGuid().ToString("N"));
        var temporaryArchive = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(staging);

        try
        {
            foreach (var (relative, source) in files)
            {
                var destination = PackagePath.Resolve(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? staging);
                File.Copy(source, destination);
            }

            ModManifestFile.Write(staging, manifest);
            ZipFile.CreateFromDirectory(staging, temporaryArchive);
            File.Move(temporaryArchive, output, overwrite: true);
        }
        finally
        {
            Directory.Delete(staging, recursive: true);
            if (File.Exists(temporaryArchive)) File.Delete(temporaryArchive);
        }
    }

    /// <summary>
    /// Builds package-level display maps from author items and conventional README files.
    /// Explicit mappings win before conventions are considered, independently of item ordering.
    /// </summary>
    private static void ApplyPresentation(
        ModManifest manifest,
        string presentationDirectory,
        string projectDirectory,
        Dictionary<string, string> files)
    {
        var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitReadmes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var explicitSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tag in File.ReadAllLines(Path.Combine(presentationDirectory, "localizations.txt")))
        {
            var language = tag.ToLowerInvariant();
            if (!languages.Add(language))
            {
                throw new ModValidationException("DuplicateTranslation", $"Duplicate localization for language '{language}'.");
            }

            AddTranslation(manifest.Name, language, ReadPresentationField(presentationDirectory, tag, "name"));
            AddTranslation(manifest.Description, language, ReadPresentationField(presentationDirectory, tag, "description"));
            var readme = ReadPresentationField(presentationDirectory, tag, "readme").Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(readme)) continue;

            AddReadme(manifest, language, readme, projectDirectory, files);
            explicitReadmes.Add(language);
            explicitSources.Add(Path.GetFullPath(files[readme]));
        }

        foreach (var path in File.ReadAllLines(Path.Combine(presentationDirectory, "readmes.txt")))
        {
            var stem = Path.GetFileNameWithoutExtension(path);
            var language = stem.Equals("README", StringComparison.OrdinalIgnoreCase) ? "en" : stem["README.".Length..].ToLowerInvariant();
            var source = files.TryGetValue(path, out var payloadSource) ? payloadSource : PackagePath.Resolve(projectDirectory, path);
            if (explicitReadmes.Contains(language) || explicitSources.Contains(Path.GetFullPath(source))) continue;

            AddReadme(manifest, language, path, projectDirectory, files);
        }
    }

    /// <summary>
    /// Reads a projected MSBuild metadata value, removing only the task's final line terminator.
    /// </summary>
    private static string ReadPresentationField(string directory, string language, string field)
    {
        var text = File.ReadAllText(Path.Combine(directory, language + "." + field + ".txt"));
        return text.EndsWith(Environment.NewLine, StringComparison.Ordinal) ? text[..^Environment.NewLine.Length] : text;
    }

    /// <summary>
    /// Leaves omitted project fields out of the manifest instead of blocking locale fallback.
    /// </summary>
    private static void AddTranslation(Dictionary<string, string> translations, string language, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) translations.Add(language, value);
    }

    /// <summary>
    /// Includes a README at its declared relative path and rejects ambiguous language/file mappings.
    /// </summary>
    private static void AddReadme(
        ModManifest manifest,
        string language,
        string path,
        string projectDirectory,
        Dictionary<string, string> files)
    {
        PackagePath.ValidateRelative(path);
        // A normal Content item may already supply this package path, including a linked shared file.
        // Otherwise the declaration includes a file at the same relative path in the author project.
        if (!files.ContainsKey(path))
        {
            var source = PackagePath.Resolve(projectDirectory, path);
            if (!File.Exists(source)) throw new ModValidationException("MissingReadme", source);

            files.Add(path, source);
        }

        if (!manifest.Readme.TryAdd(language, path))
        {
            throw new ModValidationException("DuplicateTranslation", $"Multiple README files for language '{language}'. Specify Readme explicitly.");
        }
    }

    /// <summary>
    /// Matches deployment identities without conflating satellites of different cultures.
    /// Version and public-key compatibility are checked after finding the host provider.
    /// </summary>
    private static string GetIdentityKey(AssemblyName identity) => identity.Name + "|" + identity.CultureName;
}
