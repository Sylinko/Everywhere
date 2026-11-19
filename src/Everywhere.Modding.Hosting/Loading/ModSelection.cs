using Everywhere.Modding.Packaging;
using ZLinq;

namespace Everywhere.Modding.Hosting.Loading;

/// <summary>
/// Inspects the requested packages without loading code or deciding whether the complete graph is valid.
/// Both retirement planning and subsequent activation use this same immutable file selection.
/// </summary>
internal sealed class ModSelection
{
    /// <summary>
    /// Gets requested declarations, including unsupported and invalid packages.
    /// </summary>
    internal Dictionary<string, ModDefinition> Requested { get; }

    /// <summary>
    /// Gets neutral metadata artifacts and their satellites for independently readable packages.
    /// </summary>
    internal Dictionary<string, AssemblyArtifact[]> Artifacts { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets each declared entry's inspected artifact, independently of assembly loading classification.
    /// </summary>
    internal Dictionary<string, AssemblyArtifact> Entries { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets selected logical dependencies before graph failures propagate to consumers.
    /// </summary>
    internal Dictionary<string, HashSet<string>> Dependencies { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets package-local inspection failures as host-owned values.
    /// </summary>
    internal Dictionary<string, ModDiagnostic> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Captures package identities, dependency availability and readable PE metadata for one operation.
    /// An invalid replacement remains a requested replacement rather than restoring its predecessor.
    /// </summary>
    internal ModSelection(IReadOnlyList<ModDefinition> definitions, HostAssemblyCatalog host)
    {
        Requested = definitions
            .AsValueEnumerable()
            .GroupBy(mod => mod.Id, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.AsValueEnumerable().First(),
                StringComparer.Ordinal);
        foreach (var group in definitions.AsValueEnumerable().GroupBy(mod => mod.Id).Where(group => group.Count() > 1))
        {
            Failures.Add(
                group.Key,
                new ModDiagnostic
                {
                    ModId = group.Key,
                    Code = "DuplicateMod",
                    Detail = $"Mod '{group.Key}' was selected more than once."
                });
        }

        foreach (var mod in Requested.Values)
        {
            if (mod.SkipReason is not null) continue;

            var selected = mod.Entry.Dependencies
                .AsValueEnumerable()
                .Where(dependency =>
                    Requested.TryGetValue(dependency.Id, out var target) && target.SkipReason is null &&
                    ModCompatibility.IsSatisfied(dependency.Version, target.Installation.Manifest.Version))
                .Select(dependency => dependency.Id)
                .ToHashSet(StringComparer.Ordinal);
            Dependencies.Add(mod.Id, selected);

            if (mod.Diagnostic is { } diagnostic) Failures.TryAdd(mod.Id, diagnostic);
            if (Failures.ContainsKey(mod.Id)) continue;

            try
            {
                var (artifacts, entry) = Inspect(mod, host);
                Artifacts.Add(mod.Id, artifacts);
                Entries.Add(mod.Id, entry);
            }
            catch (Exception exception) when
                (exception is ModValidationException or IOException or BadImageFormatException or UnauthorizedAccessException)
            {
                Failures.Add(
                    mod.Id,
                    new ModDiagnostic
                    {
                        ModId = mod.Id,
                        Code = exception is ModValidationException validation ? validation.Code : "PreflightFailed",
                        Detail = exception.ToString()
                    });
            }
        }
    }

    /// <summary>
    /// Computes a requesting assembly's proposed collectible bindings from the selected declarations.
    /// Availability is fixed here; later validation must not silently drop a failed optional provider.
    /// </summary>
    internal HashSet<string> GetBindings(string id, AssemblyArtifact artifact)
    {
        var mod = Requested[id];
        var local = Artifacts[id]
            .AsValueEnumerable()
            .Select(value => value.Identity.Name ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var bindings = artifact.References
            .AsValueEnumerable()
            .Select(reference => reference.Name ?? string.Empty)
            .Where(name =>
            {
                var declared = mod.Entry.Dependencies
                    .AsValueEnumerable()
                    .FirstOrDefault(dependency => dependency.Assemblies.Contains(name, StringComparer.OrdinalIgnoreCase));
                return declared is null ? local.Contains(name) : Dependencies[id].Contains(declared.Id);
            })
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        bindings.UnionWith(
            mod.Entry.Dependencies
                .Where(dependency => Dependencies[id].Contains(dependency.Id))
                .SelectMany(dependency => dependency.Assemblies));
        bindings.Remove(artifact.Identity.Name ?? string.Empty);
        return bindings;
    }

    /// <summary>
    /// Binds the declared entry by path before grouping neutral artifacts and their satellites.
    /// Entry validation uses this same metadata regardless of the artifact's loading role.
    /// </summary>
    private static (AssemblyArtifact[] Artifacts, AssemblyArtifact Entry) Inspect(ModDefinition mod, HostAssemblyCatalog host)
    {
        var artifacts = mod.Entry.Assemblies
            .AsValueEnumerable()
            .Select(path => new AssemblyArtifact(PackagePath.Resolve(mod.Installation.Directory, path)))
            .ToArray();
        var entryPath = Path.GetFullPath(PackagePath.Resolve(mod.Installation.Directory, mod.Entry.Assembly));
        var entry = artifacts.AsValueEnumerable().FirstOrDefault(artifact => artifact.Path == entryPath) ??
            throw new ModValidationException("MissingEntry", $"Entry assembly '{mod.Entry.Assembly}' is not in the selected managed files.");

        foreach (var satellite in artifacts.AsValueEnumerable().Where(artifact => !string.IsNullOrEmpty(artifact.Identity.CultureName)))
        {
            var parent = artifacts
                .AsValueEnumerable()
                .FirstOrDefault(artifact =>
                    string.IsNullOrEmpty(artifact.Identity.CultureName) && satellite.Identity.Name == artifact.Identity.Name + ".resources");
            // Culture and identity come from metadata, never directory-name heuristics.
            if (parent is null || satellite.TypeRequirements.Keys.AsValueEnumerable().Any(name => name != "<Module>") ||
                !parent.Satellites.TryAdd(satellite.Identity.CultureName ?? string.Empty, satellite))
            {
                throw new ModValidationException(
                    "InvalidSatellite",
                    $"'{satellite.Path}' is not a unique resource satellite of a selected assembly.");
            }
        }

        var neutral = artifacts.AsValueEnumerable().Where(artifact => string.IsNullOrEmpty(artifact.Identity.CultureName)).ToArray();
        foreach (var artifact in neutral)
        {
            var name = artifact.Identity.Name ?? throw new ModValidationException("InvalidAssemblyName", artifact.Path);
            if (host.Contains(name))
            {
                throw new ModValidationException("HostAssemblyInPackage", $"Mod '{mod.Id}' includes host assembly '{name}'.");
            }
        }

        return (neutral, entry);
    }
}