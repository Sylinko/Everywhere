using System.Runtime.Loader;
using Everywhere.Modding.Packaging;
using ZLinq;

namespace Everywhere.Modding.Hosting.Loading;

/// <summary>
/// Resolves native requests lazily against the NuGet <c>runtimes/{rid}/native/</c> layout and <c>.deps.json</c>.
/// Native binaries are never pre-validated or eagerly loaded at package load time.
/// </summary>
internal sealed class ModNativeResolver
{
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);

    /// <summary>
    /// Records a package's native resolution environment without inspecting or loading its native files.
    /// </summary>
    internal void Add(ModDefinition mod)
    {
        var componentPath = PackagePath.Resolve(mod.Installation.Directory, mod.Entry.Assembly);
        _sources.TryAdd(componentPath, new Source(componentPath, mod.Installation.Directory, mod.RuntimeIdentifier));
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
                throw new DllNotFoundException(
                    $"Shared assembly providers disagree on native library '{name}'. Use a separate managed wrapper assembly.");
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
    /// Resolves NuGet native assets from <c>.deps.json</c> and package-bundled <c>runtimes/{rid}/native/</c> directories.
    /// </summary>
    private sealed class Source
    {
        /// <summary>
        /// Distinguishes a managed-only provider from one that supplies package native assets for the host platform.
        /// Both participate in agreement checks so a shared wrapper cannot borrow another provider's DLL silently.
        /// </summary>
        internal bool HasNativeAssets => _nativeDirectories.Length > 0;

        private readonly string _depsJsonPath;
        private readonly string[] _nativeDirectories;
        private readonly Lazy<AssemblyDependencyResolver> _resolver;

        internal Source(string componentPath, string directory, string runtimeIdentifier)
        {
            _depsJsonPath = Path.ChangeExtension(componentPath, ".deps.json");
            _resolver = new Lazy<AssemblyDependencyResolver>(() => new AssemblyDependencyResolver(componentPath));
            _nativeDirectories = ModPlatformSelector
                .GetFallbackPlatforms(runtimeIdentifier)
                .Select(platform => Path.Combine(directory, "runtimes", platform, "native"))
                .Where(nativeDirectory => Directory.Exists(nativeDirectory) && Directory.EnumerateFiles(nativeDirectory).Any())
                .ToArray();
        }

        /// <summary>
        /// Checks <c>.deps.json</c> first and then falls back to platform-ordered <c>runtimes/{rid}/native/</c> probing.
        /// System libraries and unknown names return <see langword="null"/> so the CLR proceeds with standard OS probing.
        /// </summary>
        internal string? Resolve(string name)
        {
            if (!HasNativeAssets) return null;

            if (File.Exists(_depsJsonPath))
            {
                var resolved = _resolver.Value.ResolveUnmanagedDllToPath(name);
                if (resolved is not null && File.Exists(resolved))
                {
                    return resolved;
                }
            }

            var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var candidates = GetCandidateFileNames(name).Distinct(comparison).ToHashSet(comparison);

            foreach (var nativeDirectory in _nativeDirectories)
            {
                var matches = Directory
                    .EnumerateFiles(nativeDirectory)
                    .Where(file => candidates.Contains(Path.GetFileName(file)))
                    .ToArray();

                switch (matches.Length)
                {
                    case 0:
                        continue;
                    case 1:
                        return matches[0];
                    default:
                        throw new DllNotFoundException($"Several package files match native library '{name}'.");
                }
            }

            return null;
        }

        private static IEnumerable<string> GetCandidateFileNames(string name)
        {
            var fileName = Path.GetFileName(name);
            yield return fileName;

            if (OperatingSystem.IsWindows())
            {
                if (!fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    yield return fileName + ".dll";
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                if (!fileName.EndsWith(".dylib", StringComparison.Ordinal))
                {
                    yield return fileName + ".dylib";
                    if (!fileName.StartsWith("lib", StringComparison.Ordinal))
                    {
                        yield return "lib" + fileName + ".dylib";
                    }
                }
            }
            else
            {
                if (!fileName.EndsWith(".so", StringComparison.Ordinal))
                {
                    yield return fileName + ".so";
                    if (!fileName.StartsWith("lib", StringComparison.Ordinal))
                    {
                        yield return "lib" + fileName + ".so";
                    }
                }
            }
        }
    }
}
