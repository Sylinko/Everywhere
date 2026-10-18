using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Everywhere.Plugin.Abstractions;
using Everywhere.Plugin.Hosting.Lifecycle;
using ZLinq;

namespace Everywhere.Plugin.Hosting.Loading;

/// <summary>
/// Creates owners in dependency order and resolves only preselected, requester-authorized identities.
/// </summary>
internal sealed class AssemblyRegistry(HostAssemblyCatalog host)
{
    /// <summary>
    /// Retains unfinished cleanup after a timeout; further mutations are blocked by the engine.
    /// Completed callbacks return strings rather than retaining plugin exceptions.
    /// </summary>
    private Task<string?>? _pendingCleanup;

    private readonly ConcurrentDictionary<string, AssemblyOwner> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets physical ownership names, including owners whose plugin initialization may have failed.
    /// </summary>
    internal string[] Names => [.. _owners.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Prepares missing owners synchronously, so creation order precedes consumer type materialization.
    /// </summary>
    internal Dictionary<string, string> Prepare(PluginLoadPlan plan)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in plan.AssemblyOrder)
        {
            if (_owners.ContainsKey(name)) continue;

            if (plan.AssemblyDependencies[name].AsValueEnumerable().Any(failures.ContainsKey))
            {
                failures.Add(name, "A selected assembly dependency failed preparation.");
                continue;
            }

            try
            {
                var artifact = plan.Assemblies[name];
                var owner = new AssemblyOwner(name, this, plan.AssemblyDependencies[name], plan.NativeResolvers[name]);
                PluginResource.Attach(owner, owner.Resources);
                _owners[name] = owner;
                owner.LoadInspected(artifact);
            }
            catch (Exception exception)
            {
                failures.Add(name, exception.ToString());
            }
        }

        return failures;
    }

    /// <summary>
    /// Gets a prepared assembly without initiating selection or creating another owner.
    /// </summary>
    internal Assembly GetAssembly(string name) => _owners[name].LoadedAssembly;

    /// <summary>
    /// Establishes the plugin's resolution scope for Default-owned calls made during lifecycle callbacks.
    /// </summary>
    internal AssemblyLoadContext.ContextualReflectionScope EnterScope(string name)
    {
        return _owners[name].EnterContextualReflection();
    }

    /// <summary>
    /// Closes the retiring set and disposes each owner's static registrations before any owner is unloaded.
    /// A failed or timed-out owner leaves its dependencies intact and requires process restart.
    /// </summary>
    internal async Task<PluginActivation.CallbackResult> CleanupAsync(string[] names, TimeSpan timeout)
    {
        foreach (var name in names)
        {
            if (_owners.TryGetValue(name, out var owner)) owner.Resources.Close();
        }

        foreach (var name in names)
        {
            if (!_owners.TryGetValue(name, out var owner)) continue;

            _pendingCleanup = Task.Run(async () =>
            {
                PluginActivation.IsInCallback = true;
                try
                {
                    using var scope = owner.EnterContextualReflection();
                    await owner.Resources.DisposeAsync().ConfigureAwait(false);
                    return null;
                }
                catch (Exception exception)
                {
                    return exception.ToString();
                }
                finally
                {
                    PluginActivation.IsInCallback = false;
                }
            });

            try
            {
                var error = await _pendingCleanup.WaitAsync(timeout).ConfigureAwait(false);
                _pendingCleanup = null;
                if (error is not null) return new PluginActivation.CallbackResult(true, name + ": " + error);
            }
            catch (TimeoutException)
            {
                return new PluginActivation.CallbackResult(false, name + ": assembly resource cleanup timed out; execution was not aborted.");
            }
        }

        return new PluginActivation.CallbackResult(true, null);
    }

    /// <summary>
    /// Releases owners outside the collection loop's stack. No reflection objects escape this boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal AssemblyRetirement[] Release(IEnumerable<string> names)
    {
        var references = new List<AssemblyRetirement>();

        foreach (var name in names)
        {
            if (!_owners.TryRemove(name, out var owner)) continue;

            references.Add(new AssemblyRetirement(new WeakReference(owner, trackResurrection: true), owner.NativeLibraries));
            owner.Release();
        }

        return references.ToArray();
    }

    /// <summary>
    /// Rejects unsupported requests with FileLoadException, preventing fallback to unrelated Default copies.
    /// </summary>
    private Assembly Resolve(AssemblyName requested, HashSet<string> allowed)
    {
        var name = requested.Name ?? string.Empty;
        if (host.Contains(name)) return host.Resolve(requested);

        if (!allowed.Contains(name) || !_owners.TryGetValue(name, out var owner) ||
            !AssemblyArtifact.CanSatisfy(owner.LoadedAssembly.GetName(), requested))
        {
            throw new FileLoadException($"Assembly '{requested}' is unavailable or undeclared for the requesting owner.");
        }

        return owner.LoadedAssembly;
    }

    /// <summary>
    /// Owns one managed DLL. Resolver permissions are immutable for the owner's entire activation.
    /// </summary>
    private sealed class AssemblyOwner(
        string name,
        AssemblyRegistry registry,
        HashSet<string> allowed,
        PluginNativeResolver nativeResolver
    ) : AssemblyLoadContext(name, isCollectible: true)
    {
        /// <summary>
        /// Gets the assembly after synchronous preparation has completed.
        /// </summary>
        internal Assembly LoadedAssembly => _assembly ?? throw new InvalidOperationException("Assembly preparation has not completed.");

        /// <summary>
        /// Owns static registrations until this exact loading instance retires.
        /// </summary>
        internal PluginResource Resources { get; } = new();

        /// <summary>
        /// Tracks explicit OS loader references independently of managed context collection.
        /// </summary>
        internal NativeLibraryHandles NativeLibraries { get; } = new();

        private Assembly? _assembly;
        private IReadOnlyDictionary<string, AssemblyArtifact> _satellites = new Dictionary<string, AssemblyArtifact>();
        private readonly HashSet<string> _allowed = new(allowed, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Loads the exact bytes inspected during preflight.
        /// </summary>
        internal void LoadInspected(AssemblyArtifact artifact)
        {
            _satellites = artifact.Satellites;
            using var stream = new MemoryStream(artifact.ReadVerifiedBytes(), writable: false);
            _assembly = LoadFromStream(stream);
        }

        /// <summary>
        /// Drops the explicit Assembly reference before requesting context unloading.
        /// </summary>
        internal void Release()
        {
            PluginResource.Detach(this);
            _assembly = null;
            Unload();
        }

        /// <inheritdoc />
        protected override nint LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = nativeResolver.Resolve(unmanagedDllName);
            return path is null ? nint.Zero : NativeLibraries.Load(path, LoadUnmanagedDllFromPath);
        }

        /// <inheritdoc />
        protected override Assembly Load(AssemblyName assemblyName)
        {
            if (!string.IsNullOrEmpty(assemblyName.CultureName) && assemblyName.Name == Name + ".resources")
            {
                if (!_satellites.TryGetValue(assemblyName.CultureName, out var satellite) ||
                    !AssemblyArtifact.CanSatisfy(satellite.Identity, assemblyName))
                {
                    // ResourceManager handles a missing culture by trying its parent and then neutral resources.
                    throw new FileNotFoundException($"Resource satellite '{assemblyName}' is unavailable.");
                }

                using var stream = new MemoryStream(satellite.ReadVerifiedBytes(), writable: false);
                return LoadFromStream(stream);
            }

            return registry.Resolve(assemblyName, _allowed);
        }
    }
}