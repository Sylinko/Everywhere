using System.Text.Json;
using System.Text.Json.Serialization;

namespace Everywhere.Modding.Packaging;

/// <summary>
/// Reads and writes the generated manifest and validates data crossing the package boundary.
/// </summary>
public static class ModManifestFile
{
    /// <summary>
    /// Gets the manifest filename shared by directory and ZIP packages.
    /// </summary>
    public const string FileName = "mod.json";

    private static readonly PackagingJsonSerializerContext JsonContext = new(
        new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            RespectNullableAnnotations = true,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip
        });

    /// <summary>
    /// Reads a complete declaration without loading its assemblies.
    /// </summary>
    public static ModManifest Read(string directory)
    {
        using var stream = File.OpenRead(Path.Combine(directory, FileName));
        return Read(stream);
    }

    /// <summary>
    /// Reads and validates a manifest from an archive entry or file stream.
    /// </summary>
    public static ModManifest Read(Stream stream)
    {
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
    /// Checks serialized identities, metadata, ranges, and mutually exclusive entry declarations.
    /// </summary>
    public static void Validate(ModManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !ModValidationRules.IsValidIdentifier(manifest.Id))
        {
            throw new ModValidationException("InvalidManifest", "Unsupported schema or invalid mod ID.");
        }

        ModCompatibility.ParseVersion(manifest.Version);
        ModValidationRules.ValidateTranslations(manifest.Name);
        ModValidationRules.ValidateTranslations(manifest.Description);
        ModValidationRules.ValidateTranslations(manifest.Readme);

        foreach (var path in manifest.Readme.Values)
        {
            PackagePath.ValidateRelative(path);
            if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                throw new ModValidationException("InvalidReadme", "README declarations must reference Markdown files.");
            }
        }

        ModValidationRules.ValidateAuthor(manifest.Author);
        if (manifest.Icon is not null)
        {
            PackagePath.ValidateRelative(manifest.Icon);
            if (!ModValidationRules.IsSupportedIconExtension(manifest.Icon))
            {
                throw new ModValidationException("InvalidIcon", $"Unsupported icon format '{manifest.Icon}'.");
            }
        }

        if (manifest.Homepage is not null)
        {
            ModValidationRules.ValidateHttpUri(manifest.Homepage);
        }

        ModValidationRules.ValidateRepository(manifest.Repository);
        ModValidationRules.ValidateLicense(manifest.License);
        ValidateEntryDeclaration(manifest.Entry, manifest.Id);
    }

    /// <summary>
    /// Validates universal or per-platform entry declarations shared between <c>mod.json</c> and <c>index.json</c>.
    /// </summary>
    internal static void ValidateEntryDeclaration(ModEntry entry, string modId)
    {
        switch (entry)
        {
            case UniversalModEntry universal:
            {
                ValidateEntry(universal, modId);
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

                    ValidateEntry(variant, modId);
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

        // Omitted dependency collections keep their empty defaults; explicit JSON null is rejected.
        if (entry.Dependencies is null)
        {
            throw new JsonException("Dependency collection cannot be null.");
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in entry.Assemblies)
        {
            PackagePath.ValidateRelative(path);

            if (!paths.Add(path))
            {
                throw new ModValidationException("DuplicateAsset", $"Duplicate selected assembly '{path}'.");
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
            if (!ModValidationRules.IsValidIdentifier(dependency.Id) || dependency.Id == modId || !dependencies.Add(dependency.Id))
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
}
