using System.Runtime.Loader;
using Autofac.Core;
using ZLinq;

namespace Everywhere.Modding.Hosting.Services;

/// <summary>
/// Removes Autofac reflection entries belonging to retiring assembly owners, including generic
/// arguments whose defining assembly is the host. Live owners are never selected wholesale.
/// </summary>
internal static class ModReflectionCache
{
    // Autofac stores ReflectionCacheSet.Shared behind a WeakReference that is normally only rooted
    // by live containers. Autofac.Extensions.DependencyInjection registers static external caches
    // into ReflectionCacheSet.Shared once from a type initializer; if every container is disposed
    // and GC collects that ReflectionCacheSet before another container is built, subsequent
    // ReflectionCacheSet.Shared.Clear calls target a fresh instance that no longer tracks those
    // static external caches, leaking collectible mod types for the rest of the process.
    private static readonly ReflectionCacheSet Shared = ReflectionCacheSet.Shared;

    /// <summary>
    /// Anchors Autofac's shared reflection cache set so static external caches registered on it
    /// remain reachable across container lifetimes.
    /// </summary>
    internal static void EnsurePinned() => _ = Shared;

    /// <summary>
    /// Clears only keys or recorded assemblies referring to an owner that is actually retiring.
    /// Activation scopes and assembly resources must have finished disposal before this call.
    /// </summary>
    internal static void Clear(IReadOnlySet<AssemblyLoadContext> contexts)
    {
        if (contexts.Count == 0)
        {
            return;
        }

        Shared.Clear((key, assemblies) =>
            assemblies.AsValueEnumerable().Any(assembly => CollectibleContextMatcher.References(assembly, contexts)) ||
            CollectibleContextMatcher.References(key, contexts));
    }
}
