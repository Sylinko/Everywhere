using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting.Loading;

/// <summary>
/// Reserves declared host files and the running framework in Default before any collectible owners exist.
/// </summary>
public sealed class HostAssemblyCatalog
{
    private readonly Dictionary<string, AssemblyArtifact> _assemblies = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates a catalog from deployment-declared runtime files. The plugin contract and running
    /// core framework are always host-owned; application dependencies must be provided by the caller.
    /// </summary>
    public HostAssemblyCatalog(IEnumerable<string> assemblyPaths)
    {
        var frameworkDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var trustedPaths = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty).Split(Path.PathSeparator);
        var frameworkPaths = trustedPaths.Where(path => Path.GetDirectoryName(path) == frameworkDirectory);
        var contractPath = typeof(IPlugin).Assembly.Location;

        foreach (var path in assemblyPaths.Concat(frameworkPaths).Append(contractPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var artifact = new AssemblyArtifact(path);
            var name = artifact.Identity.Name ?? throw new PluginValidationException("InvalidAssemblyName", path);

            if (_assemblies.TryGetValue(name, out var existing) && existing.Fingerprint != artifact.Fingerprint)
            {
                throw new PluginValidationException("HostAssemblyConflict", $"Host assembly '{name}' has different candidate files.");
            }

            _assemblies[name] = artifact;
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
            _assemblies.TryGetValue(name, out var artifact) &&
            AssemblyArtifact.CanSatisfy(artifact.Identity, requested);
    }

    /// <summary>
    /// Returns the unique host instance, rejecting a differing assembly already resident in Default.
    /// </summary>
    internal Assembly Resolve(AssemblyName requested)
    {
        if (requested.Name is not { } name ||
            !_assemblies.TryGetValue(name, out var artifact) ||
            !AssemblyArtifact.CanSatisfy(artifact.Identity, requested))
        {
            throw new FileLoadException($"Host assembly '{requested}' is not available in the deployment catalog.");
        }

        var existing = AssemblyLoadContext.Default.Assemblies.FirstOrDefault(assembly => string.Equals(
            assembly.GetName().Name,
            name,
            StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            if (!AssemblyArtifact.CanSatisfy(existing.GetName(), artifact.Identity) ||
                existing.ManifestModule.ModuleVersionId != ReadModuleVersionId(artifact.Path))
            {
                throw new FileLoadException($"Default already contains a different '{name}' assembly.");
            }

            return existing;
        }

        artifact.ReadVerifiedBytes();
        return AssemblyLoadContext.Default.LoadFromAssemblyPath(artifact.Path);
    }

    /// <summary>
    /// Compares the selected host file with an already loaded module without retaining PE resources.
    /// </summary>
    private static Guid ReadModuleVersionId(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();
        return reader.GetGuid(reader.GetModuleDefinition().Mvid);
    }
}