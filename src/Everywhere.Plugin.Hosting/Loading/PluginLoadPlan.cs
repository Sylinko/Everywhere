using System.Reflection;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Hosting.Loading;

/// <summary>
/// Validates metadata and dependency graphs before creating any collectible context.
/// </summary>
internal sealed class PluginLoadPlan
{
    /// <summary>
    /// Gets logical plugins indexed by stable identifier.
    /// </summary>
    internal IReadOnlyDictionary<string, PluginDefinition> Plugins { get; }

    /// <summary>
    /// Gets desired declarations, including packages skipped before inspecting their assemblies.
    /// </summary>
    internal IReadOnlyDictionary<string, PluginDefinition> RequestedPlugins { get; }

    /// <summary>
    /// Gets lazy native resolution environments for each managed assembly owner.
    /// </summary>
    internal Dictionary<string, PluginNativeResolver> NativeResolvers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets one selected artifact per managed simple name.
    /// </summary>
    internal Dictionary<string, AssemblyArtifact> Assemblies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the selected dependency identifiers for each plugin activation.
    /// </summary>
    internal Dictionary<string, HashSet<string>> Dependencies { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets direct allowed collectible bindings for each assembly owner.
    /// </summary>
    internal Dictionary<string, HashSet<string>> AssemblyDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets each plugin's owned or shared managed simple names.
    /// </summary>
    internal Dictionary<string, HashSet<string>> PluginAssemblies { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets dependency-first assembly creation order.
    /// </summary>
    internal IReadOnlyList<string> AssemblyOrder { get; }

    /// <summary>
    /// Gets dependency-first plugin initialization order.
    /// </summary>
    internal IReadOnlyList<string> PluginOrder { get; }

    /// <summary>
    /// Builds the two acyclic graphs, retaining absent optional declarations on their plugin definitions.
    /// </summary>
    internal PluginLoadPlan(IReadOnlyList<PluginDefinition> definitions, HostAssemblyCatalog host)
    {
        var requested = new Dictionary<string, PluginDefinition>(StringComparer.Ordinal);
        var plugins = new Dictionary<string, PluginDefinition>(StringComparer.Ordinal);
        foreach (var plugin in definitions)
        {
            if (!requested.TryAdd(plugin.Id, plugin))
            {
                throw new PluginValidationException("DuplicatePlugin", $"Plugin '{plugin.Id}' was selected more than once.");
            }

            if (plugin.SkipReason is not null) continue;
            plugins.Add(plugin.Id, plugin);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var artifacts = plugin.Entry.Assemblies
                .Select(path => new AssemblyArtifact(PackagePath.Resolve(plugin.Installation.Directory, path)))
                .ToArray();

            foreach (var satellite in artifacts.Where(artifact => !string.IsNullOrEmpty(artifact.Identity.CultureName)))
            {
                var parent = artifacts.FirstOrDefault(artifact =>
                    string.IsNullOrEmpty(artifact.Identity.CultureName) &&
                    satellite.Identity.Name == artifact.Identity.Name + ".resources");

                // Culture and assembly identity come from PE metadata, not directory-name heuristics.
                // Executable culture-specific libraries need a separate policy; this path is resource-only.
                if (parent is null ||
                    satellite.TypeRequirements.Keys.Any(name => name != "<Module>") ||
                    !parent.Satellites.TryAdd(satellite.Identity.CultureName ?? string.Empty, satellite))
                {
                    throw new PluginValidationException(
                        "InvalidSatellite",
                        $"'{satellite.Path}' is not a unique resource satellite of a selected assembly.");
                }
            }

            foreach (var artifact in artifacts.Where(artifact => string.IsNullOrEmpty(artifact.Identity.CultureName)))
            {
                var name = artifact.Identity.Name ?? throw new PluginValidationException("InvalidAssemblyName", artifact.Path);

                if (host.Contains(name))
                {
                    throw new PluginValidationException("HostAssemblyInPackage", $"Plugin '{plugin.Id}' includes host assembly '{name}'.");
                }

                if (Assemblies.TryGetValue(name, out var selected) && !selected.HasSameContent(artifact))
                {
                    throw new PluginValidationException("AssemblyConflict", $"Different selected files define '{name}'.");
                }

                if (!NativeResolvers.TryGetValue(name, out var resolver))
                {
                    resolver = new PluginNativeResolver();
                    NativeResolvers.Add(name, resolver);
                }

                resolver.Add(plugin);
                Assemblies.TryAdd(name, artifact);
                names.Add(name);
            }

            PluginAssemblies.Add(plugin.Id, names);
        }

        RequestedPlugins = requested;
        Plugins = plugins;

        foreach (var plugin in plugins.Values)
        {
            var selected = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dependency in plugin.Entry.Dependencies)
            {
                if (plugins.TryGetValue(dependency.Id, out var target) &&
                    PluginCompatibility.IsSatisfied(dependency.Version, target.Installation.Manifest.Version))
                {
                    selected.Add(dependency.Id);

                    if (dependency.Assemblies.Any(name => !PluginAssemblies[dependency.Id].Contains(name)))
                    {
                        throw new PluginValidationException(
                            "InvalidDependencyMapping",
                            $"Dependency '{dependency.Id}' does not provide its declared assemblies.");
                    }
                }
                else if (!dependency.IsOptional)
                {
                    throw new PluginValidationException("MissingDependency", $"Plugin '{plugin.Id}' requires compatible '{dependency.Id}'.");
                }
            }

            Dependencies.Add(plugin.Id, selected);
        }

        foreach (var (name, artifact) in Assemblies)
        {
            var owners = plugins.Values.Where(plugin => PluginAssemblies[plugin.Id].Contains(name)).ToArray();
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var reference in artifact.References)
            {
                var referenceName = reference.Name ?? "";

                if (host.Contains(referenceName))
                {
                    if (!host.CanSatisfy(reference))
                    {
                        throw new PluginValidationException("HostIdentityMismatch", $"'{name}' requires unavailable host identity '{reference}'.");
                    }

                    continue;
                }

                var canBind = true;
                var hasSelectedBinding = false;

                foreach (var owner in owners)
                {
                    var declared = owner.Entry.Dependencies.FirstOrDefault(dependency =>
                        dependency.Assemblies.Contains(referenceName, StringComparer.OrdinalIgnoreCase));
                    var isLocal = PluginAssemblies[owner.Id].Contains(referenceName);

                    if (declared is not null && !Dependencies[owner.Id].Contains(declared.Id))
                    {
                        canBind = false;
                        continue;
                    }

                    if (!isLocal && declared is null)
                    {
                        throw new PluginValidationException(
                            "UndeclaredDependency",
                            $"'{name}' references undeclared '{referenceName}' in '{owner.Id}'.");
                    }

                    hasSelectedBinding = true;
                }

                if (!canBind && hasSelectedBinding)
                {
                    throw new PluginValidationException(
                        "SharedBindingConflict",
                        $"Shared '{name}' has inconsistent optional bindings for '{referenceName}'.");
                }

                if (!canBind) continue;

                if (!Assemblies.TryGetValue(referenceName, out var dependencyArtifact) ||
                    !AssemblyArtifact.CanSatisfy(dependencyArtifact.Identity, reference))
                {
                    throw new PluginValidationException("MissingAssembly", $"'{name}' requires '{reference}'.");
                }

                allowed.Add(referenceName);
            }

            // Dynamic external bindings must be declared too. Shared owners must agree on availability.
            foreach (var owner in owners)
            {
                foreach (var dependency in owner.Entry.Dependencies.Where(d => Dependencies[owner.Id].Contains(d.Id)))
                {
                    foreach (var dependencyName in dependency.Assemblies)
                    {
                        if (owners.Any(other => !other.Entry.Dependencies.Any(d => d.Id == dependency.Id &&
                                Dependencies[other.Id].Contains(d.Id) && d.Assemblies.Contains(dependencyName, StringComparer.OrdinalIgnoreCase))))
                        {
                            throw new PluginValidationException(
                                "SharedBindingConflict",
                                $"Shared '{name}' has inconsistent declarations for '{dependencyName}'.");
                        }

                        if (!string.Equals(name, dependencyName, StringComparison.OrdinalIgnoreCase)) allowed.Add(dependencyName);
                    }
                }
            }

            foreach (var required in artifact.DeclaredRequirements)
            {
                if (owners.Any(owner => owner.Entry.Dependencies.All(d => d.Id != required)))
                {
                    throw new PluginValidationException("UndeclaredRequirement", $"'{name}' has an undeclared RequiresPlugin condition.");
                }
            }

            AssemblyDependencies.Add(name, allowed);
        }

        AssemblyOrder = Sort(AssemblyDependencies, "AssemblyCycle");
        PluginOrder = Sort(Dependencies, "PluginCycle");

        foreach (var plugin in plugins.Values)
        {
            var entry = GetEntry(plugin);

            if (!entry.EntryCandidates.Contains(plugin.Entry.Type) ||
                entry.TypeRequirements[plugin.Entry.Type].Any(id => !Dependencies[plugin.Id].Contains(id)))
            {
                throw new PluginValidationException(
                    "InvalidEntryType",
                    $"Entry '{plugin.Entry.Type}' is unavailable or is not a public concrete class with a parameterless constructor.");
            }
        }
    }

    /// <summary>
    /// Gets the inspected entry assembly by its selected package-relative path.
    /// </summary>
    internal AssemblyArtifact GetEntry(PluginDefinition plugin)
    {
        var entryPath = PackagePath.Resolve(plugin.Installation.Directory, plugin.Entry.Assembly);
        var entryName = AssemblyName.GetAssemblyName(entryPath).Name ?? string.Empty;
        return Assemblies[entryName];
    }

    /// <summary>
    /// Produces a stable dependency-first order and reports the actual directed cycle.
    /// </summary>
    private static List<string> Sort(Dictionary<string, HashSet<string>> graph, string code)
    {
        var completed = new HashSet<string>(graph.Comparer);
        var path = new List<string>();
        var order = new List<string>();

        foreach (var name in graph.Keys.Order(StringComparer.Ordinal))
        {
            Visit(name);
        }

        return order;

        void Visit(string name)
        {
            if (completed.Contains(name)) return;
            var cycleIndex = path.FindIndex(value => graph.Comparer.Equals(value, name));

            if (cycleIndex >= 0)
            {
                throw new PluginValidationException(code, string.Join(" -> ", path.Skip(cycleIndex).Append(name)));
            }

            path.Add(name);

            foreach (var dependency in graph[name].Order(StringComparer.Ordinal))
            {
                Visit(dependency);
            }

            path.RemoveAt(path.Count - 1);
            completed.Add(name);
            order.Add(name);
        }
    }
}