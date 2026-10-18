using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Sdk;

/// <summary>
/// Packages explicit build outputs while excluding host-owned managed identities.
/// </summary>
internal static class PluginPackager
{
    /// <summary>
    /// Creates a ZIP and generated manifest from resolved build inputs, never from a hand-maintained manifest.
    /// </summary>
    public static void Pack(
        string assemblyPath,
        string referenceList,
        string payloadList,
        string pluginId,
        string version,
        string entryType,
        string framework,
        string hostRange,
        string apiRange,
        string platform,
        string outputPath)
    {
        var host = JsonSerializer.Deserialize(
            File.ReadAllText(referenceList + ".host.json"),
            SdkJsonSerializerContext.Default.PluginHostDescriptor)
            ?? throw new PluginValidationException("MissingHostImport", "The build has no host import record.");
        var hostAssemblies = File.ReadAllLines(referenceList + ".runtime.txt").ToDictionary(
            path => AssemblyName.GetAssemblyName(path).Name ?? "",
            AssemblyName.GetAssemblyName,
            StringComparer.OrdinalIgnoreCase);
        var hostImplementationNames = File.ReadAllLines(referenceList)
            .Select(path => AssemblyName.GetAssemblyName(path).Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var managed = new List<string>();
        var native = new List<string>();
        var content = new List<string>();
        var hasHostImplementationReference = false;
        var contractName = typeof(Abstractions.IPlugin).Assembly.GetName().Name;

        foreach (var outputItem in File.ReadAllLines(payloadList).Append(assemblyPath + "|" + Path.GetFileName(assemblyPath))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var parts = outputItem.Split('|');
            var path = parts[0];
            var name = parts[1].Replace('\\', '/');
            PackagePath.ValidateRelative(name);
            if (!File.Exists(path)) throw new PluginValidationException("MissingBuildOutput", path);

            var identity = ReadManagedIdentity(path);
            if (identity is not null)
            {
                if (!string.IsNullOrEmpty(identity.CultureName) && identity.Name is { } satelliteName &&
                    satelliteName.EndsWith(".resources", StringComparison.Ordinal) &&
                    hostAssemblies.ContainsKey(satelliteName[..^".resources".Length]))
                {
                    continue;
                }

                if (hostAssemblies.TryGetValue(identity.Name ?? "", out var provided))
                {
                    if (provided.Version < identity.Version ||
                        !(provided.GetPublicKeyToken() ?? []).SequenceEqual(identity.GetPublicKeyToken() ?? []))
                    {
                        throw new PluginValidationException(
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
            else if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     parts.Length > 2 && parts[2] == "native" && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                native.Add(name);
            }
            else
            {
                content.Add(name);
            }

            if (!files.TryAdd(name, path) && files[name] != path)
            {
                throw new PluginValidationException("PackagePathConflict", $"Different build outputs map to '{name}'.");
            }
        }

        if (hasHostImplementationReference && string.IsNullOrWhiteSpace(hostRange))
        {
            throw new PluginValidationException(
                "MissingHostRange",
                "Packaging requires EverywhereHostVersion; an exact range for the imported host is a valid starting point.");
        }

        if (!string.IsNullOrWhiteSpace(hostRange) && !PluginCompatibility.IsSatisfied(hostRange, host.HostVersion))
        {
            throw new PluginValidationException("BuildHostOutsideRange", "The declared host range excludes the imported build host.");
        }

        if (native.Count != 0 && string.IsNullOrEmpty(platform))
        {
            throw new PluginValidationException("NativePlatformRequired", "Native assets require an explicit EverywherePluginPlatform and matching RuntimeIdentifier.");
        }

        var entryAssembly = Path.GetFileName(assemblyPath);
        if (string.IsNullOrWhiteSpace(apiRange))
        {
            var apiVersion = PluginCompatibility.ParseVersion(host.PluginApiVersion);
            apiRange = apiVersion.IsPrerelease ?
                $"[{apiVersion.ToNormalizedString()}]" :
                $"[{apiVersion.ToNormalizedString()},{(long)apiVersion.Major + 1}.0.0-0)";
        }

        if (!PluginCompatibility.IsSatisfied(apiRange, host.PluginApiVersion))
        {
            throw new PluginValidationException("BuildApiOutsideRange", "The declared Plugin API range excludes the imported build contract.");
        }

        PluginEntry entry = string.IsNullOrEmpty(platform) ?
            new UniversalPluginEntry
            {
                Assembly = entryAssembly,
                Type = entryType,
                Framework = framework,
                PluginApiVersion = apiRange,
                HostVersion = string.IsNullOrWhiteSpace(hostRange) ? null : hostRange,
                Assemblies = [.. managed.Distinct(StringComparer.OrdinalIgnoreCase)],
                NativeLibraries = [.. native.Distinct(StringComparer.OrdinalIgnoreCase)],
                ContentFiles = [.. content.Distinct(StringComparer.OrdinalIgnoreCase)]
            } :
            new PlatformPluginEntry
            {
                Variants =
                [
                    new PluginPlatformVariant
                    {
                        Platform = platform,
                        Assembly = entryAssembly,
                        Type = entryType,
                        Framework = framework,
                        PluginApiVersion = apiRange,
                        HostVersion = string.IsNullOrWhiteSpace(hostRange) ? null : hostRange,
                        Assemblies = [.. managed.Distinct(StringComparer.OrdinalIgnoreCase)],
                        NativeLibraries = [.. native.Distinct(StringComparer.OrdinalIgnoreCase)],
                        ContentFiles = [.. content.Distinct(StringComparer.OrdinalIgnoreCase)]
                    }
                ]
            };
        var manifest = new PluginManifest { Id = pluginId, Version = version, Entry = entry };
        PluginManifestFile.Validate(manifest);
        var output = Path.GetFullPath(outputPath);
        var parent = Path.GetDirectoryName(output) ?? ".";
        Directory.CreateDirectory(parent);
        var staging = Path.Combine(parent, ".plugin-pack-" + Guid.NewGuid().ToString("N"));
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

            PluginManifestFile.Write(staging, manifest);
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
    /// Classifies DLLs by PE metadata, without treating executables or supporting content as managed assemblies.
    /// </summary>
    private static AssemblyName? ReadManagedIdentity(string path)
    {
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return null;

        try
        {
            return AssemblyName.GetAssemblyName(path);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}