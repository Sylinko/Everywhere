using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Avalonia;
using Avalonia.Threading;
using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting.Lifecycle;
using Everywhere.Modding.Hosting.Services;
using ZLinq;

namespace Everywhere.Modding.Hosting.Loading;

/// <summary>
/// Creates owners in dependency order and resolves only preselected, requester-authorized identities.
/// </summary>
internal sealed class AssemblyRegistry(HostAssemblyCatalog host)
{
    /// <summary>
    /// Gets physical ownership names, including owners whose mod initialization may have failed.
    /// </summary>
    internal string[] Names => [.. _owners.Keys.Order(StringComparer.Ordinal)];

    /// <summary>
    /// Retains unfinished cleanup after a timeout; further mutations are blocked by the engine.
    /// Completed callbacks return strings rather than retaining mod exceptions.
    /// </summary>
    private Task<string?>? _pendingCleanup;

    private readonly ConcurrentDictionary<string, AssemblyOwner> _owners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Prepares missing owners synchronously, so creation order precedes consumer type materialization.
    /// </summary>
    internal Dictionary<string, string> Prepare(ModLoadPlan plan)
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
                ModResource.Attach(owner, owner.Resources);
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
    /// Gets the prepared entry owner for the activation's service lifetime scope.
    /// </summary>
    internal AssemblyLoadContext GetContext(string name) => _owners[name];

    /// <summary>
    /// Establishes the mod's resolution scope for Default-owned calls made during lifecycle callbacks.
    /// </summary>
    internal AssemblyLoadContext.ContextualReflectionScope EnterScope(string name) => _owners[name].EnterContextualReflection();

    /// <summary>
    /// Closes the retiring set and disposes each owner's static registrations before any owner is unloaded.
    /// A failed or timed-out owner leaves its dependencies intact and requires process restart.
    /// </summary>
    internal async Task<ModActivation.CallbackResult> CleanupAsync(string[] names, TimeSpan timeout)
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
                ModActivation.IsInCallback = true;
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
                    ModActivation.IsInCallback = false;
                }
            });

            try
            {
                var error = await _pendingCleanup.WaitAsync(timeout).ConfigureAwait(false);
                _pendingCleanup = null;
                if (error is not null) return new ModActivation.CallbackResult(true, $"{name}: {error}");
            }
            catch (TimeoutException)
            {
                return new ModActivation.CallbackResult(false, $"{name}: assembly resource cleanup timed out; execution was not aborted.");
            }
        }

        return new ModActivation.CallbackResult(true, null);
    }

    /// <summary>
    /// Drains pending Avalonia UI work and sweeps UI and non-UI framework caches for retiring owners
    /// after assembly resource disposal has finished and before owners are released.
    /// </summary>
    internal Task<ModActivation.CallbackResult> SweepAvaloniaAsync(string[] names, TimeSpan timeout)
    {
        if (!HasRetiringOwners(names))
        {
            return Task.FromResult(new ModActivation.CallbackResult(true, null));
        }

        return SweepContextsCoreAsync(
            () => SweepOwners(names, CollectibleAssemblyCleanup.SweepUI),
            () => SweepOwners(names, CollectibleAssemblyCleanup.Sweep),
            timeout);
    }

    /// <summary>
    /// Drains pending Avalonia UI work and re-sweeps UI and non-UI caches for any released contexts
    /// whose <see cref="WeakReference"/> remains alive across GC retry attempts.
    /// Strong <see cref="AssemblyLoadContext"/> references stay isolated inside non-inlined helpers.
    /// </summary>
    internal static Task<ModActivation.CallbackResult> SweepAliveAvaloniaAsync(
        AssemblyRetirement[] references,
        TimeSpan timeout
    )
    {
        if (!HasAliveContexts(references))
        {
            return Task.FromResult(new ModActivation.CallbackResult(true, null));
        }

        return SweepContextsCoreAsync(
            () => SweepRetirements(references, CollectibleAssemblyCleanup.SweepUI),
            () => SweepRetirements(references, CollectibleAssemblyCleanup.Sweep),
            timeout);
    }

    private static async Task<ModActivation.CallbackResult> SweepContextsCoreAsync(
        Action sweepUi,
        Action sweepNonUi,
        TimeSpan timeout
    )
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        try
        {
            if (Application.Current is not null)
            {
                try
                {
                    await Dispatcher.UIThread
                        .InvokeAsync(sweepUi, DispatcherPriority.ContextIdle, timeoutCts.Token)
                        .GetTask()
                        .WaitAsync(timeout, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!timeoutCts.IsCancellationRequested)
                {
                    sweepUi();
                }
            }
            else
            {
                // Headless or uninitialized hosts without a message loop have no dispatcher pump to wait on.
                sweepUi();
            }

            sweepNonUi();
            return new ModActivation.CallbackResult(true, null);
        }
        catch (TimeoutException)
        {
            return new ModActivation.CallbackResult(false, "Avalonia context sweep timed out.");
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            return new ModActivation.CallbackResult(false, "Avalonia context sweep timed out.");
        }
        catch (Exception exception)
        {
            return new ModActivation.CallbackResult(true, exception.ToString());
        }
    }

    private bool HasRetiringOwners(string[] names)
    {
        foreach (var name in names)
        {
            if (_owners.ContainsKey(name)) return true;
        }

        return false;
    }

    private static bool HasAliveContexts(AssemblyRetirement[] references)
    {
        foreach (var reference in references)
        {
            if (reference.Context.IsAlive) return true;
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private void SweepOwners(string[] names, Action<IReadOnlySet<AssemblyLoadContext>> sweep)
    {
        var contexts = SnapshotContexts(names);
        if (contexts.Count != 0) sweep(contexts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SweepRetirements(AssemblyRetirement[] references, Action<IReadOnlySet<AssemblyLoadContext>> sweep)
    {
        var contexts = SnapshotAliveContexts(references);
        if (contexts.Count != 0) sweep(contexts);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static HashSet<AssemblyLoadContext> SnapshotAliveContexts(AssemblyRetirement[] references)
    {
        var contexts = new HashSet<AssemblyLoadContext>();
        foreach (var reference in references)
        {
            if (reference.Context.Target is AssemblyLoadContext context)
            {
                contexts.Add(context);
            }
        }

        return contexts;
    }

    /// <summary>
    /// Releases owners outside the collection loop's stack. No reflection objects escape this boundary.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal ReleaseResult Release(string[] names)
    {
        var contexts = SnapshotContexts(names);
        List<string>? errors = null;

        RunCleanupStep(() => ModReflectionCache.Clear(contexts), ref errors);
        RunCleanupStep(() => RefreshTypeDescriptors(contexts), ref errors);

        var references = new List<AssemblyRetirement>();

        foreach (var name in names)
        {
            if (!_owners.TryRemove(name, out var owner)) continue;

            references.Add(new AssemblyRetirement(new WeakReference(owner, trackResurrection: true), owner.NativeLibraries));
            owner.Release();
        }

        return new ReleaseResult([.. references], errors is null ? null : string.Join(Environment.NewLine, errors));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private HashSet<AssemblyLoadContext> SnapshotContexts(string[] names) =>
        names.AsValueEnumerable().Where(_owners.ContainsKey).Select(AssemblyLoadContext (name) => _owners[name]).ToHashSet();

    private static void RunCleanupStep(Action step, ref List<string>? errors)
    {
        try
        {
            step();
        }
        catch (Exception exception)
        {
            (errors ??= []).Add(exception.ToString());
        }
    }

    private static void RefreshTypeDescriptors(HashSet<AssemblyLoadContext> contexts)
    {
        foreach (var context in contexts)
        {
            foreach (var assembly in context.Assemblies)
            {
                TypeDescriptor.Refresh(assembly);
            }
        }
    }

    /// <summary>
    /// Captures retired context tracking references alongside any synchronous framework cleanup error.
    /// </summary>
    internal sealed record ReleaseResult(AssemblyRetirement[] Retirements, string? Error);

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
        ModNativeResolver nativeResolver
    ) : AssemblyLoadContext(name, isCollectible: true)
    {
        /// <summary>
        /// Gets the assembly after synchronous preparation has completed.
        /// </summary>
        internal Assembly LoadedAssembly => _assembly ?? throw new InvalidOperationException("Assembly preparation has not completed.");

        /// <summary>
        /// Owns static registrations until this exact loading instance retires.
        /// </summary>
        internal ModResource Resources { get; } = new();

        /// <summary>
        /// Tracks explicit OS loader references independently of managed context collection.
        /// </summary>
        internal NativeLibraryHandles NativeLibraries { get; } = new();

        private Assembly? _assembly;
        private Dictionary<string, AssemblyArtifact> _satellites = new();
        private readonly HashSet<string> _allowed = new(allowed, StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Loads the inspected assembly stream without keeping OS file locks on package binaries.
        /// </summary>
        internal void LoadInspected(AssemblyArtifact artifact)
        {
            _satellites = artifact.Satellites;
            using var stream = File.OpenRead(artifact.Path);
            _assembly = LoadFromStream(stream);
        }

        /// <summary>
        /// Drops the explicit Assembly reference before requesting context unloading.
        /// </summary>
        internal void Release()
        {
            ModResource.Detach(this);
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

                using var stream = File.OpenRead(satellite.Path);
                return LoadFromStream(stream);
            }

            return registry.Resolve(assemblyName, _allowed);
        }
    }
}
