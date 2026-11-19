using System.Runtime.Loader;
using Everywhere.Modding.Packaging;
using ZLinq;

namespace Everywhere.Modding.Hosting.Loading;

/// <summary>
/// Resolves native requests against the selected packages only when native code is first requested.
/// Package metadata is a file inventory, not a native dependency graph or a list of libraries to preload.
/// </summary>
internal sealed class ModNativeResolver
{
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a package's resolution environment without inspecting or loading its native files.
    /// </summary>
    internal void Add(ModDefinition mod)
    {
        var componentPath = PackagePath.Resolve(mod.Installation.Directory, mod.Entry.Assembly);
        _sources.TryAdd(componentPath, new Source(componentPath, mod.Installation.Directory, mod.Entry.NativeLibraries));
    }

    /// <summary>
    /// Existing owners keep a stable environment. A provider change retires shared consumers together;
    /// changing an unrelated, unused native file does not require comparing every native binary at startup.
    /// </summary>
    internal bool HasSameSources(ModNativeResolver other)
    {
        if (!_sources.Values.AsValueEnumerable().Any(source => source.HasNativeAssets) &&
            !other._sources.Values.AsValueEnumerable().Any(source => source.HasNativeAssets))
        {
            return true;
        }

        return _sources.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(other._sources.Keys);
    }

    /// <summary>
    /// Resolves the requested name in each provider. Shared wrappers may bind only when providers agree
    /// on the requested library; unrelated native assets do not participate in that decision.
    /// </summary>
    internal string? Resolve(string name)
    {
        var paths = _sources.Values.AsValueEnumerable().Select(source => source.Resolve(name)).ToArray();
        var selected = paths.AsValueEnumerable().FirstOrDefault(path => path is not null);
        if (selected is null) return null;

        foreach (var path in paths)
        {
            if (path is null || path != selected && !HasSameBytes(selected, path))
            {
                throw new DllNotFoundException($"Shared assembly providers disagree on native library '{name}'. Use a separate managed wrapper assembly.");
            }
        }

        return selected;
    }

    /// <summary>
    /// Compares only an actually requested duplicate. Packages remain immutable while their owners are resident.
    /// </summary>
    private static bool HasSameBytes(string first, string second)
    {
        if (new FileInfo(first).Length != new FileInfo(second).Length) return false;
        return File.ReadAllBytes(first).AsSpan().SequenceEqual(File.ReadAllBytes(second));
    }

    /// <summary>
    /// Uses the .NET dependency resolver for NuGet assets and explicit build outputs for project content.
    /// Lazy construction avoids parsing native resolution metadata for code paths that never use native code.
    /// </summary>
    private sealed class Source(string componentPath, string directory, string[] nativeFiles)
    {
        /// <summary>
        /// Distinguishes a managed-only provider from one that supplies a native resolution environment.
        /// Both participate in agreement checks so a shared wrapper cannot borrow another provider's DLL silently.
        /// </summary>
        internal bool HasNativeAssets => nativeFiles.Length > 0;

        private readonly Lazy<AssemblyDependencyResolver> _resolver = new(() => new AssemblyDependencyResolver(componentPath));

        /// <summary>
        /// Falls back to declared output paths for files copied by a project rather than a NuGet runtime asset.
        /// System libraries and unknown names return to the CLR's normal platform probing.
        /// </summary>
        internal string? Resolve(string name)
        {
            if (!HasNativeAssets) return null;

            var resolved = _resolver.Value.ResolveUnmanagedDllToPath(name);
            if (resolved is not null) return resolved;

            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var matches = nativeFiles
                .AsValueEnumerable()
                .Where(path =>
                    string.Equals(Path.GetFileName(path), name, comparison) ||
                    OperatingSystem.IsWindows() && string.Equals(Path.GetFileName(path), name + ".dll", comparison))
                .ToArray();

            return matches.Length switch
            {
                0 => null,
                1 => PackagePath.Resolve(directory, matches[0]),
                _ => throw new DllNotFoundException($"Several package files match native library '{name}'.")
            };
        }
    }
}