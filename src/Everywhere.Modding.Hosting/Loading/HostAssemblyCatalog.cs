using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Packaging;
using ZLinq;

namespace Everywhere.Modding.Hosting.Loading;

/// <summary>
/// Reserves declared host files and the running framework in Default before any collectible owners exist.
/// Inspects only PE assembly headers and module version IDs to avoid reading large framework binaries into memory.
/// </summary>
public sealed class HostAssemblyCatalog
{
    private readonly Dictionary<string, HostAssemblyEntry> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a catalog from deployment-declared runtime files. The mod contract and running
    /// core framework are always host-owned; application dependencies must be provided by the caller.
    /// </summary>
    public HostAssemblyCatalog(IEnumerable<string> assemblyPaths)
    {
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var trustedPaths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty).Split(Path.PathSeparator);
        var frameworkPaths = trustedPaths.Where(path => Path.GetDirectoryName(path) == frameworkDirectory);
        var contractPath = typeof(IMod).Assembly.Location;
        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        foreach (var path in assemblyPaths
                     .AsValueEnumerable()
                     .Concat(frameworkPaths)
                     .Append(contractPath)
                     .Where(path => !string.IsNullOrEmpty(path))
                     .Select(Path.GetFullPath)
                     .Distinct(comparison))
        {
            var entry = ReadHostAssembly(path);
            var name = entry.Identity.Name ?? throw new ModValidationException("InvalidAssemblyName", path);

            if (_assemblies.TryGetValue(name, out var existing) && existing.Mvid != entry.Mvid)
            {
                throw new ModValidationException("HostAssemblyConflict", $"Host assembly '{name}' has different candidate files.");
            }

            _assemblies[name] = entry;
        }
    }

    /// <summary>
    /// Returns whether the host owns the requested simple name, including files not loaded yet.
    /// </summary>
    public bool Contains(string assemblyName) => _assemblies.ContainsKey(assemblyName);

    /// <summary>
    /// Checks metadata without triggering a load into Default.
    /// </summary>
    internal bool CanSatisfy(AssemblyName requested)
    {
        return requested.Name is { } name &&
            _assemblies.TryGetValue(name, out var entry) &&
            AssemblyArtifact.CanSatisfy(entry.Identity, requested);
    }

    /// <summary>
    /// Returns the unique host instance, rejecting a differing assembly already resident in Default.
    /// </summary>
    internal Assembly Resolve(AssemblyName requested)
    {
        if (requested.Name is not { } name ||
            !_assemblies.TryGetValue(name, out var entry) ||
            !AssemblyArtifact.CanSatisfy(entry.Identity, requested))
        {
            throw new FileLoadException($"Host assembly '{requested}' is not available in the deployment catalog.");
        }

        var existing = AssemblyLoadContext.Default.Assemblies
            .AsValueEnumerable()
            .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!AssemblyArtifact.CanSatisfy(existing.GetName(), entry.Identity) ||
                existing.ManifestModule.ModuleVersionId != entry.Mvid)
            {
                throw new FileLoadException($"Default already contains a different '{name}' assembly.");
            }

            return existing;
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(entry.Path);
    }

    /// <summary>
    /// Reads only PE assembly identity and module version ID without hashing or decoding full metadata tables.
    /// </summary>
    private static HostAssemblyEntry ReadHostAssembly(string fullPath)
    {
        using var stream = File.OpenRead(fullPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata)
        {
            throw new ModValidationException("NotManagedAssembly", $"'{fullPath}' is not a managed assembly.");
        }

        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
        {
            throw new ModValidationException("NotManagedAssembly", $"'{fullPath}' is not an assembly manifest.");
        }

        var definition = reader.GetAssemblyDefinition();
        var name = reader.GetString(definition.Name);
        if (string.IsNullOrEmpty(name))
        {
            throw new ModValidationException("InvalidAssemblyName", fullPath);
        }

        var identity = new AssemblyName
        {
            Name = name,
            Version = definition.Version,
            CultureName = reader.GetString(definition.Culture)
        };
        identity.SetPublicKey(reader.GetBlobBytes(definition.PublicKey));
        var mvid = reader.GetGuid(reader.GetModuleDefinition().Mvid);

        return new HostAssemblyEntry(fullPath, identity, mvid);
    }

    private sealed record HostAssemblyEntry(string Path, AssemblyName Identity, Guid Mvid);
}
