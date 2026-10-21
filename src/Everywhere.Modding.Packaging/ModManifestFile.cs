using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Reads and writes the generated manifest and validates data crossing the package boundary.
/// </summary>
public static partial class ModManifestFile
{
    /// <summary>
    /// Gets the manifest filename shared by directory and ZIP packages.
    /// </summary>
    public const string FileName = "mod.json";

    private static readonly PackagingJsonSerializerContext JsonContext = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    });

    /// <summary>
    /// Reads a complete declaration without loading its assemblies.
    /// </summary>
    public static ModManifest Read(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, FileName));
        var manifest = JsonSerializer.Deserialize(stream, JsonContext.ModManifest);
        if (manifest is null)
        {
            throw new ModValidationException("MissingManifest", "The package manifest is empty.");
        }

        Validate(manifest);
        return manifest;
    }

    /// <summary>
    /// Writes SDK-generated metadata after validating its structure.
    /// </summary>
    public static void Write(string directory, ModManifest manifest)
    {
        Validate(manifest);
        using var stream = File.Create(Path.Combine(directory, FileName));
        JsonSerializer.Serialize(stream, manifest, JsonContext.ModManifest);
    }

    /// <summary>
    /// Checks serialized identities, ranges and mutually exclusive entry declarations.
    /// </summary>
    public static void Validate(ModManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !IdentifierPattern().IsMatch(manifest.Id))
        {
            throw new ModValidationException("InvalidManifest", "Unsupported schema or invalid mod ID.");
        }

        ModCompatibility.ParseVersion(manifest.Version);
        foreach (var translations in new[] { manifest.Name, manifest.Description, manifest.Readme })
        {
            foreach (var (language, value) in translations)
            {
                if (!LanguageTagPattern().IsMatch(language) || string.IsNullOrWhiteSpace(value))
                {
                    throw new ModValidationException("InvalidLocalization", $"Invalid language tag or empty value for '{language}'.");
                }
            }
        }

        foreach (var path in manifest.Readme.Values)
        {
            PackagePath.ValidateRelative(path);
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModValidationException("InvalidReadme", "README declarations must reference Markdown files.");
            }
        }

        switch (manifest.Entry)
        {
            case UniversalModEntry universal:
            {
                ValidateEntry(universal, manifest.Id);
                break;
            }
            case PlatformModEntry platform:
            {
                var platforms = new HashSet<string>(StringComparer.Ordinal);
                if (platform.Variants.Length == 0)
                {
                    throw new ModValidationException("MissingEntry", "A platform package needs at least one entry.");
                }

                foreach (var variant in platform.Variants)
                {
                    if (!ModPlatformSelector.IsKnownPlatform(variant.Platform) || !platforms.Add(variant.Platform))
                    {
                        throw new ModValidationException("InvalidPlatform", $"Unknown or duplicate platform '{variant.Platform}'.");
                    }

                    ValidateEntry(ModPlatformSelector.GetImplementation(variant), manifest.Id);
                }
                break;
            }
            default:
            {
                throw new ModValidationException("InvalidEntry", "The entry declaration is missing or unsupported.");
            }
        }
    }

    /// <summary>
    /// Checks declarations before filesystem or CLR work begins.
    /// </summary>
    private static void ValidateEntry(UniversalModEntry entry, string modId)
    {
        if (string.IsNullOrWhiteSpace(entry.Type) || string.IsNullOrWhiteSpace(entry.Framework))
        {
            throw new ModValidationException("InvalidEntry", "Entry type and framework must be specified.");
        }

        ModCompatibility.ParseRange(entry.ModApiVersion);
        if (entry.HostVersion is { } hostVersion)
        {
            ModCompatibility.ParseRange(hostVersion);
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
                throw new ModValidationException("DuplicateAsset", $"Duplicate selected asset '{path}'.");
            }
        }

        PackagePath.ValidateRelative(entry.Assembly);
        if (!entry.Assemblies.Contains(entry.Assembly, StringComparer.Ordinal))
        {
            throw new ModValidationException("MissingEntry", "The entry assembly must be in its managed file selection.");
        }

        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in entry.Dependencies)
        {
            if (!IdentifierPattern().IsMatch(dependency.Id) || dependency.Id == modId || !dependencies.Add(dependency.Id))
            {
                throw new ModValidationException("InvalidDependency", $"Invalid or duplicate dependency '{dependency.Id}'.");
            }

            ModCompatibility.ParseRange(dependency.Version);
            foreach (var assembly in dependency.Assemblies)
            {
                if (string.IsNullOrWhiteSpace(assembly) || assembly.IndexOfAny(['/', '\\', ',']) >= 0)
                {
                    throw new ModValidationException("InvalidDependencyAssembly", $"Invalid simple assembly name '{assembly}'.");
                }
            }
        }
    }

    /// <summary>
    /// Checks the portable language-tag spelling without restricting authors to the host's current languages.
    /// </summary>
    [GeneratedRegex("^[A-Za-z]{1,8}(?:-[A-Za-z0-9]{1,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex LanguageTagPattern();

    /// <summary>
    /// Restricts mod IDs to portable directory-safe logical identifiers.
    /// </summary>
    [GeneratedRegex("^[a-z0-9]+(?:[._-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();
}