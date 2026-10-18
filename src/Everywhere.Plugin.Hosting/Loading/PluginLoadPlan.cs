using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Packaging;
using ZLinq;

namespace Everywhere.Plugin.Hosting.Loading;

/// <summary>
/// Validates the replacement graph against survivors before creating new collectible owners.
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
    /// Gets rejected and dependency-blocked requests without retaining exception instances.
    /// </summary>
    internal Dictionary<string, PluginState> Failures { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Gets all independent preflight findings, attributed to their logical owners.
    /// </summary>
    internal List<PluginDiagnostic> Diagnostics { get; } = [];

    /// <summary>
    /// Retains entry metadata selected by package path, including entries that fail type validation.
    /// </summary>
    private readonly Dictionary<string, AssemblyArtifact> _entries;

    /// <summary>
    /// Validates the activation graph after retirement. Surviving physical owners are fixed;
    /// conflicting newcomers fail without expanding the already completed shutdown scope.
    /// </summary>
    internal PluginLoadPlan(PluginSelection selection, HostAssemblyCatalog host, PluginLoadPlan? previous, IReadOnlySet<string> active)
    {
        RequestedPlugins = selection.Requested;
        _entries = selection.Entries;
        var plugins = selection.Requested.Values
            .AsValueEnumerable()
            .Where(plugin => plugin.SkipReason is null)
            .ToDictionary(plugin => plugin.Id, StringComparer.Ordinal);
        Plugins = plugins;
        var resident = previous is null ?
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) :
            active.SelectMany(id => previous.PluginAssemblies[id]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, diagnostic) in selection.Failures) Fail(id, diagnostic.Code, diagnostic.Detail);

        foreach (var plugin in plugins.Values)
        {
            PluginAssemblies.Add(
                plugin.Id,
                selection.Artifacts
                    .GetValueOrDefault(plugin.Id, [])
                    .AsValueEnumerable()
                    .Select(artifact => artifact.Identity.Name ?? string.Empty)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase));
            Dependencies.Add(plugin.Id, selection.Dependencies[plugin.Id]);
        }

        foreach (var group in selection.Artifacts
                     .AsValueEnumerable()
                     .SelectMany(pair => pair.Value.AsValueEnumerable().Select(artifact => (Id: pair.Key, Artifact: artifact)))
                     .GroupBy(item => item.Artifact.Identity.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase))
        {
            var artifact = resident.Contains(group.Key) && previous is not null ?
                previous.Assemblies[group.Key] :
                group.AsValueEnumerable().First().Artifact;
            var hasConflict = group.AsValueEnumerable().Any(item => !item.Artifact.HasSameContent(artifact));
            foreach (var item in group)
            {
                if (hasConflict && (!resident.Contains(group.Key) || !item.Artifact.HasSameContent(artifact)))
                {
                    Fail(item.Id, "AssemblyConflict", $"Selected files conflict with the chosen owner of '{group.Key}'.");
                }
            }

            Assemblies.Add(group.Key, artifact);
        }

        foreach (var plugin in plugins.Values)
        {
            foreach (var dependency in plugin.Entry.Dependencies)
            {
                if (Dependencies[plugin.Id].Contains(dependency.Id))
                {
                    if (!Failures.ContainsKey(dependency.Id) &&
                        dependency.Assemblies.AsValueEnumerable().Any(name => !PluginAssemblies[dependency.Id].Contains(name)))
                    {
                        Fail(plugin.Id, "InvalidDependencyMapping", $"Dependency '{dependency.Id}' does not provide its declared assemblies.");
                    }
                }
                else if (!dependency.IsOptional)
                {
                    Fail(plugin.Id, "MissingDependency", $"Plugin '{plugin.Id}' requires compatible '{dependency.Id}'.", PluginState.Blocked);
                }
            }
        }

        PropagateFailures();

        foreach (var (name, artifact) in Assemblies)
        {
            var owners = plugins.Values
                .AsValueEnumerable()
                .Where(plugin => PluginAssemblies[plugin.Id].Contains(name) && !Failures.ContainsKey(plugin.Id))
                .ToArray();
            var bindings = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var owner in owners)
            {
                if (active.Contains(owner.Id) && previous is not null)
                {
                    bindings.Add(owner.Id, previous.AssemblyDependencies[name]);
                    continue;
                }

                try
                {
                    ValidateBindings(artifact, owner, host);
                    bindings.Add(owner.Id, selection.GetBindings(owner.Id, artifact));
                }
                catch (Exception exception) when (IsValidationFailure(exception))
                {
                    Fail(owner.Id, exception);
                }
            }

            var allowed = resident.Contains(name) && previous is not null ?
                previous.AssemblyDependencies[name] :
                bindings.Values.FirstOrDefault() ?? [];
            if (bindings.Values.AsValueEnumerable().Any(value => !value.SetEquals(allowed)))
            {
                foreach (var (id, value) in bindings)
                {
                    if (!resident.Contains(name) || !value.SetEquals(allowed))
                    {
                        Fail(id, "SharedBindingConflict", $"Shared '{name}' has inconsistent selected bindings.");
                    }
                }
            }

            AssemblyDependencies.Add(name, allowed);
            if (!resident.Contains(name) || previous is null) continue;

            foreach (var owner in owners.AsValueEnumerable().Where(owner => !active.Contains(owner.Id) && !Failures.ContainsKey(owner.Id)))
            {
                var resolver = new PluginNativeResolver();
                foreach (var id in previous.PluginAssemblies.Keys.AsValueEnumerable().Where(id => previous.PluginAssemblies[id].Contains(name)))
                {
                    resolver.Add(previous.Plugins[id]);
                }

                resolver.Add(owner);
                if (!previous.NativeResolvers[name].HasSameSources(resolver))
                {
                    Fail(
                        owner.Id,
                        "ResidentNativeBindingConflict",
                        $"Sharing '{name}' would change a surviving owner's native resolution environment.");
                }
            }
        }


        foreach (var plugin in plugins.Values.AsValueEnumerable().Where(plugin => !active.Contains(plugin.Id) && !Failures.ContainsKey(plugin.Id)))
        {
            var entry = GetEntry(plugin);
            if (!entry.EntryCandidates.Contains(plugin.Entry.Type) ||
                entry.TypeRequirements[plugin.Entry.Type].AsValueEnumerable().Any(id => !Dependencies[plugin.Id].Contains(id)))
            {
                Fail(
                    plugin.Id,
                    "InvalidEntryType",
                    $"Entry '{plugin.Entry.Type}' is unavailable or is not a public concrete class with a parameterless constructor.");
            }
        }

        var assemblyOrder = Sort(
            AssemblyDependencies,
            cycle =>
            {
                foreach (var id in plugins.Keys.AsValueEnumerable().Where(id => PluginAssemblies[id].Overlaps(cycle)))
                {
                    Fail(id, "AssemblyCycle", string.Join(" -> ", cycle));
                }
            });
        var pluginOrder = Sort(
            Dependencies,
            cycle =>
            {
                foreach (var id in cycle)
                {
                    Fail(id, "PluginCycle", string.Join(" -> ", cycle));
                }
            });

        PropagateFailures();
        foreach (var id in Failures.Keys)
        {
            plugins.Remove(id);
            Dependencies.Remove(id);
            PluginAssemblies.Remove(id);
        }

        var retained = PluginAssemblies.Values.AsValueEnumerable().SelectMany(names => names).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Assemblies.Keys.AsValueEnumerable().Where(name => !retained.Contains(name)).ToArray())
        {
            Assemblies.Remove(name);
            AssemblyDependencies.Remove(name);
        }

        foreach (var name in Assemblies.Keys)
        {
            if (resident.Contains(name) && previous is not null)
            {
                NativeResolvers.Add(name, previous.NativeResolvers[name]);
                continue;
            }

            var resolver = new PluginNativeResolver();
            foreach (var plugin in plugins.Values.AsValueEnumerable().Where(plugin => PluginAssemblies[plugin.Id].Contains(name)))
            {
                resolver.Add(plugin);
            }

            NativeResolvers.Add(name, resolver);
        }

        AssemblyOrder = assemblyOrder.AsValueEnumerable().Where(retained.Contains).ToArray();
        PluginOrder = pluginOrder.AsValueEnumerable().Where(plugins.ContainsKey).ToArray();
    }

    /// <summary>
    /// Blocks consumers after their selected provider fails without changing optional selection.
    /// </summary>
    private void PropagateFailures()
    {
        var hasChanges = true;

        while (hasChanges)
        {
            hasChanges = false;

            foreach (var (id, dependencies) in Dependencies)
            {
                if (Failures.ContainsKey(id)) continue;

                var unavailable = dependencies.AsValueEnumerable().Where(Failures.ContainsKey).ToArray();
                if (unavailable.Length == 0) continue;

                Fail(id, "DependencyFailed", $"Selected dependencies failed: {string.Join(", ", unavailable)}.", PluginState.Blocked);
                hasChanges = true;
            }
        }
    }

    /// <summary>
    /// Validates one requesting plugin's references without granting another provider's undeclared bindings.
    /// Shared owners are compared separately, against the fixed resident environment when present.
    /// </summary>
    private void ValidateBindings(AssemblyArtifact artifact, PluginDefinition owner, HostAssemblyCatalog host)
    {
        foreach (var reference in artifact.References)
        {
            var name = reference.Name ?? string.Empty;
            if (host.Contains(name))
            {
                if (!host.CanSatisfy(reference))
                {
                    throw new PluginValidationException(
                        "HostIdentityMismatch",
                        $"'{artifact.Identity.Name}' requires unavailable host identity '{reference}'.");
                }

                continue;
            }

            var declared = owner.Entry.Dependencies
                .AsValueEnumerable()
                .FirstOrDefault(dependency => dependency.Assemblies.Contains(name, StringComparer.OrdinalIgnoreCase));
            if (declared is not null && !Dependencies[owner.Id].Contains(declared.Id))
            {
                continue;
            }
            if (declared is null && !PluginAssemblies[owner.Id].Contains(name))
            {
                throw new PluginValidationException(
                    "UndeclaredDependency",
                    $"'{artifact.Identity.Name}' references undeclared '{name}' in '{owner.Id}'.");
            }

            if (!Assemblies.TryGetValue(name, out var target) || !AssemblyArtifact.CanSatisfy(target.Identity, reference))
            {
                throw new PluginValidationException("MissingAssembly", $"'{artifact.Identity.Name}' requires '{reference}'.");
            }
        }

        if (artifact.DeclaredRequirements
            .AsValueEnumerable()
            .Any(required => owner.Entry.Dependencies.AsValueEnumerable().All(dependency => dependency.Id != required)))
        {
            throw new PluginValidationException(
                "UndeclaredRequirement",
                $"'{artifact.Identity.Name}' has an undeclared RequiresPlugin condition in '{owner.Id}'.");
        }
    }

    /// <summary>
    /// Gets the entry metadata already selected by package-relative path without reopening the file.
    /// </summary>
    internal AssemblyArtifact GetEntry(PluginDefinition plugin) => _entries[plugin.Id];

    /// <summary>
    /// Identifies failures originating in package files or metadata at the preflight boundary.
    /// </summary>
    private static bool IsValidationFailure(Exception exception)
    {
        return exception is PluginValidationException or IOException or BadImageFormatException or UnauthorizedAccessException;
    }

    /// <summary>
    /// Converts a package exception into host-owned diagnostic text.
    /// </summary>
    private void Fail(string id, Exception exception)
    {
        Fail(id, exception is PluginValidationException validation ? validation.Code : "PreflightFailed", exception.ToString());
    }

    /// <summary>
    /// Records the first causal failure for a plugin; dependency propagation does not overwrite it.
    /// </summary>
    private void Fail(string id, string code, string detail, PluginState state = PluginState.Failed)
    {
        if (!Failures.TryAdd(id, state)) return;
        Diagnostics.Add(new PluginDiagnostic
        {
            PluginId = id,
            Code = code,
            Detail = detail
        });
    }

    /// <summary>
    /// Produces a stable dependency-first order while reporting every encountered directed cycle.
    /// Cyclic nodes and their consumers are excluded before this order becomes executable.
    /// </summary>
    private static List<string> Sort(Dictionary<string, HashSet<string>> graph, Action<string[]> reportCycle)
    {
        var completed = new HashSet<string>(graph.Comparer);
        var path = new List<string>();
        var order = new List<string>();
        foreach (var name in graph.Keys.AsValueEnumerable().Order(StringComparer.Ordinal))
        {
            Visit(name);
        }

        return order;

        void Visit(string name)
        {
            if (completed.Contains(name) || !graph.TryGetValue(name, out var dependencies)) return;

            var cycleIndex = path.FindIndex(value => graph.Comparer.Equals(value, name));
            if (cycleIndex >= 0)
            {
                reportCycle(path.AsValueEnumerable().Skip(cycleIndex).ToArray());
                return;
            }

            path.Add(name);
            foreach (var dependency in dependencies.AsValueEnumerable().Order(StringComparer.Ordinal))
            {
                Visit(dependency);
            }

            path.RemoveAt(path.Count - 1);
            completed.Add(name);
            order.Add(name);
        }
    }
}