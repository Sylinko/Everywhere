using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Everywhere.Plugin.Packaging;

/// <summary>
/// Reads and writes the generated manifest and validates data crossing the package boundary.
/// </summary>
public static partial class PluginManifestFile
{
    /// <summary>
    /// Gets the manifest filename shared by directory and ZIP packages.
    /// </summary>
    public const string FileName = "plugin.json";

    private static readonly PackagingJsonSerializerContext JsonContext = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    /// <summary>
    /// Reads a complete declaration without loading its assemblies.
    /// </summary>
    public static PluginManifest Read(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, FileName));
        var manifest = JsonSerializer.Deserialize(stream, JsonContext.PluginManifest);
        if (manifest is null)
        {
            throw new PluginValidationException("MissingManifest", "The package manifest is empty.");
        }

        Validate(manifest);
        return manifest;
    }

    /// <summary>
    /// Writes SDK-generated metadata after validating its structure.
    /// </summary>
    public static void Write(string directory, PluginManifest manifest)
    {
        Validate(manifest);
        using var stream = File.Create(Path.Combine(directory, FileName));
        JsonSerializer.Serialize(stream, manifest, JsonContext.PluginManifest);
    }

    /// <summary>
    /// Checks serialized identities, ranges and mutually exclusive entry declarations.
    /// </summary>
    public static void Validate(PluginManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !IdentifierPattern().IsMatch(manifest.Id))
        {
            throw new PluginValidationException("InvalidManifest", "Unsupported schema or invalid plugin ID.");
        }

        PluginCompatibility.ParseVersion(manifest.Version);
        switch (manifest.Entry)
        {
            case UniversalPluginEntry universal:
            {
                ValidateEntry(universal, manifest.Id);
                break;
            }
            case PlatformPluginEntry platform:
            {
                var platforms = new HashSet<string>(StringComparer.Ordinal);
                if (platform.Variants.Length == 0)
                {
                    throw new PluginValidationException("MissingEntry", "A platform package needs at least one entry.");
                }

                foreach (var variant in platform.Variants)
                {
                    if (!PluginPlatformSelector.IsKnownPlatform(variant.Platform) || !platforms.Add(variant.Platform))
                    {
                        throw new PluginValidationException("InvalidPlatform", $"Unknown or duplicate platform '{variant.Platform}'.");
                    }

                    ValidateEntry(PluginPlatformSelector.GetImplementation(variant), manifest.Id);
                }
                break;
            }
            default:
            {
                throw new PluginValidationException("InvalidEntry", "The entry declaration is missing or unsupported.");
            }
        }
    }

    /// <summary>
    /// Checks declarations before filesystem or CLR work begins.
    /// </summary>
    private static void ValidateEntry(UniversalPluginEntry entry, string pluginId)
    {
        if (string.IsNullOrWhiteSpace(entry.Type) || string.IsNullOrWhiteSpace(entry.Framework))
        {
            throw new PluginValidationException("InvalidEntry", "Entry type and framework must be specified.");
        }

        PluginCompatibility.ParseRange(entry.PluginApiVersion);
        if (entry.HostVersion is { } hostVersion)
        {
            PluginCompatibility.ParseRange(hostVersion);
        }

        // Generated parameterized construction can bypass nullable checks on deferred setter assignments.
        // Omitted dependency/asset collections keep their empty defaults; explicit JSON null is rejected.
        if (entry.Dependencies is null || entry.NativeLibraries is null || entry.ContentFiles is null)
        {
            throw new JsonException("Dependency and asset collections cannot be null.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in entry.Assemblies.Concat(entry.NativeLibraries).Concat(entry.ContentFiles))
        {
            PackagePath.ValidateRelative(path);

            if (!paths.Add(path))
            {
                throw new PluginValidationException("DuplicateAsset", $"Duplicate selected asset '{path}'.");
            }
        }

        PackagePath.ValidateRelative(entry.Assembly);
        if (!entry.Assemblies.Contains(entry.Assembly, StringComparer.Ordinal))
        {
            throw new PluginValidationException("MissingEntry", "The entry assembly must be in its managed file selection.");
        }

        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in entry.Dependencies)
        {
            if (!IdentifierPattern().IsMatch(dependency.Id) || dependency.Id == pluginId || !dependencies.Add(dependency.Id))
            {
                throw new PluginValidationException("InvalidDependency", $"Invalid or duplicate dependency '{dependency.Id}'.");
            }

            PluginCompatibility.ParseRange(dependency.Version);
            foreach (var assembly in dependency.Assemblies)
            {
                if (string.IsNullOrWhiteSpace(assembly) || assembly.IndexOfAny(['/', '\\', ',']) >= 0)
                {
                    throw new PluginValidationException("InvalidDependencyAssembly", $"Invalid simple assembly name '{assembly}'.");
                }
            }
        }
    }

    /// <summary>
    /// Restricts plugin IDs to portable directory-safe logical identifiers.
    /// </summary>
    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}