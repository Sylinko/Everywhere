using ZLinq;

namespace Everywhere.Modding.Hosting.Loading;

/// <summary>
/// Determines the complete shutdown scope before mutation. Only reverse consumers and owners
/// whose actual environment changes are included; a new conflicting provider cannot evict them.
/// </summary>
internal sealed class ModRetirementPlan
{
    /// <summary>
    /// Gets active instances that must stop, in consumer-first order.
    /// </summary>
    internal string[] ModOrder { get; }

    /// <summary>
    /// Gets owners no longer used by a surviving activation, in consumer-first order.
    /// </summary>
    internal string[] AssemblyOrder { get; }

    /// <summary>
    /// Compares requested replacements and bindings against the currently loaded generation.
    /// Removal remains effective even when the replacement graph will later fail validation.
    /// </summary>
    internal ModRetirementPlan(ModSelection selection, ModLoadPlan previous, IEnumerable<string> activeIds)
    {
        var active = activeIds.ToHashSet(StringComparer.Ordinal);
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var changedAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var id in active)
        {
            var original = previous.Mods[id];
            if (!selection.Requested.TryGetValue(id, out var replacement) || replacement.SkipReason is not null ||
                selection.Failures.ContainsKey(id))
            {
                affected.Add(id);
                continue;
            }

            if (original.Installation.InstallationId != replacement.Installation.InstallationId ||
                original.Entry.Assembly != replacement.Entry.Assembly || original.Entry.Type != replacement.Entry.Type ||
                !previous.Dependencies[id].SetEquals(selection.Dependencies[id]))
            {
                affected.Add(id);
            }

            var artifacts = selection.Artifacts[id];
            if (!previous.ModAssemblies[id].SetEquals(artifacts.Select(artifact => artifact.Identity.Name ?? string.Empty)))
            {
                affected.Add(id);
            }

            foreach (var artifact in artifacts)
            {
                var name = artifact.Identity.Name ?? string.Empty;
                // Newly introduced names are validated against survivors after shutdown. They do
                // not authorize replacing another mod's owner outside this retirement scope.
                if (!previous.ModAssemblies[id].Contains(name)) continue;

                if (!artifact.HasSameContent(previous.Assemblies[name]) ||
                    !selection.GetBindings(id, artifact).SetEquals(previous.AssemblyDependencies[name]))
                {
                    changedAssemblies.Add(name);
                }
            }
        }

        foreach (var name in previous.Assemblies.Keys)
        {
            var resolver = new ModNativeResolver();
            foreach (var id in previous.ModAssemblies.Keys.AsValueEnumerable().Where(id => previous.ModAssemblies[id].Contains(name)))
            {
                if (!selection.Artifacts.TryGetValue(id, out var artifacts) ||
                    !artifacts.AsValueEnumerable().Any(artifact => string.Equals(artifact.Identity.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                resolver.Add(selection.Requested[id]);
            }

            if (!previous.NativeResolvers[name].HasSameSources(resolver)) changedAssemblies.Add(name);
        }

        var hasChanges = true;
        while (hasChanges)
        {
            hasChanges = false;
            foreach (var (name, dependencies) in previous.AssemblyDependencies)
            {
                if (dependencies.Overlaps(changedAssemblies) && changedAssemblies.Add(name)) hasChanges = true;
            }

            foreach (var id in active)
            {
                if ((previous.ModAssemblies[id].Overlaps(changedAssemblies) || previous.Dependencies[id].Overlaps(affected)) && affected.Add(id))
                {
                    hasChanges = true;
                }
            }
        }

        var retained = active
            .AsValueEnumerable()
            .Where(id => !affected.Contains(id))
            .SelectMany(id => previous.ModAssemblies[id])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ModOrder = previous.ModOrder
            .AsValueEnumerable()
            .Reverse()
            .Where(affected.Contains)
            .ToArray();
        AssemblyOrder = previous.AssemblyOrder
            .AsValueEnumerable()
            .Reverse()
            .Where(name => !retained.Contains(name))
            .ToArray();
    }
}