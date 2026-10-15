using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Everywhere.Plugin.Abstractions;

/// <summary>
/// Collects disposable resources. Host-managed containers hold handles that reverse application changes
/// when an activation or assembly is unloaded; static registration selects the assembly container.
/// </summary>
/// <remarks>
/// Disposing a registered handle is how its change is reversed; it is distinct from disposing the
/// plugin's own files, connections or background work. Process exit does not drain host-managed containers.
/// A manually created container is owned by its caller and releases its resources when explicitly disposed.
/// The names Register and DisposeAsync describe the resource operations; no separate undo interface is required.
/// </remarks>
public sealed class PluginResource : IAsyncDisposable
{
    /// <summary>
    /// Associates managed loading contexts without making the lookup itself a permanent assembly root.
    /// Only Hosting establishes these associations, before executing code in a new owner.
    /// </summary>
    private static readonly ConditionalWeakTable<AssemblyLoadContext, PluginResource> AssemblyResources = new();

    /// <summary>
    /// Serializes registration and closing; resource callbacks always run outside this lock.
    /// </summary>
    private readonly Lock _gate = new();

    /// <summary>
    /// Contains accepted resources, or null once the single disposal operation has taken ownership.
    /// </summary>
    private List<object>? _resources = [];

    /// <summary>
    /// Prevents new registrations while the owner drains its outstanding work.
    /// </summary>
    private bool _isClosed;

    /// <summary>
    /// Transfers a disposable resource to this container. For a host-managed container, the resource
    /// represents reversal of an application change, not automatic process-exit resource release.
    /// Async disposal is preferred when both interfaces are implemented. Register each responsibility
    /// once; rejected resources remain caller-owned.
    /// </summary>
    public void Register(object resource)
    {
        if (resource is not IDisposable && resource is not IAsyncDisposable)
        {
            throw new ArgumentException("The resource must implement IDisposable or IAsyncDisposable.", nameof(resource));
        }

        lock (_gate)
        {
            if (_isClosed || _resources is null)
            {
                throw new InvalidOperationException("Resource registration is closed.");
            }

            _resources.Add(resource);
        }
    }

    /// <summary>
    /// Registers a change-reversal handle with the lifetime of the assembly defining TOwner, rather
    /// than the plugin that first used it. Use a marker declared in the intended plugin library,
    /// not a framework generic constructed with a plugin argument. Process exit does not drain this container.
    /// </summary>
    public static void Register<TOwner>(object resource) => Register(typeof(TOwner), resource);

    /// <summary>
    /// Registers a change-reversal handle with the assembly defining owner, independent of the triggering
    /// plugin. Its disposal waits until that assembly is retired; process exit skips registered reversal.
    /// </summary>
    public static void Register(Type owner, object resource) => Register(owner.Assembly, resource);

    /// <summary>
    /// Registers a change-reversal handle with the host-managed loading instance of owner. Shared
    /// assembly registrations survive removal of individual consumers. Unmanaged owners and retired
    /// lifetimes are rejected; no process-wide or application-exit container is implicitly created.
    /// </summary>
    public static void Register(Assembly owner, object resource)
    {
        var context = AssemblyLoadContext.GetLoadContext(owner);
        if (context is null || !AssemblyResources.TryGetValue(context, out var resources))
        {
            throw new InvalidOperationException("The owner assembly has no managed plugin resource lifetime.");
        }

        resources.Register(resource);
    }

    /// <summary>
    /// Stops registration without releasing resources. The owner must finish using them before disposal.
    /// </summary>
    public void Close()
    {
        lock (_gate)
        {
            _isClosed = true;
        }
    }

    /// <summary>
    /// Closes registration and disposes resources sequentially in reverse order, preferring DisposeAsync.
    /// For host-managed containers, this executes the registered reversals during hot-unload cleanup.
    /// All resources are attempted before failures are aggregated. The owner must await this operation;
    /// subsequent calls do not start another disposal or wait for a concurrent first call.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        List<object>? resources;
        lock (_gate)
        {
            _isClosed = true;
            resources = _resources;
            _resources = null;
        }
        if (resources is null)
        {
            return ValueTask.CompletedTask;
        }

        List<Exception>? failures = null;
        for (var index = resources.Count - 1; index >= 0; index--)
        {
            var resource = resources[index];
            resources.RemoveAt(index);

            try
            {
                var pending = DisposeResource(resource);
                if (!pending.IsCompletedSuccessfully) return AwaitRemainingAsync(pending, resources, failures);

                // Consume even synchronous IValueTaskSource completions exactly once.
                pending.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        return failures is null ? ValueTask.CompletedTask : ValueTask.FromException(new AggregateException(failures));
    }

    /// <summary>
    /// Attaches the container before any static or module initialization can run in this loading context.
    /// </summary>
    internal static void Attach(AssemblyLoadContext owner, PluginResource resources) => AssemblyResources.Add(owner, resources);

    /// <summary>
    /// Removes the registration route after successful cleanup and before physical unloading.
    /// </summary>
    internal static void Detach(AssemblyLoadContext owner) => AssemblyResources.Remove(owner);

    /// <summary>
    /// Selects one disposal contract from the runtime object, independent of the registration expression's type.
    /// As in MSDI asynchronous scope disposal, prefer IAsyncDisposable without falling back after failure.
    /// This does not deduplicate resources shared with another container or with plugin entry disposal.
    /// </summary>
    private static ValueTask DisposeResource(object resource)
    {
        switch (resource)
        {
            case IAsyncDisposable asyncDisposable:
            {
                return asyncDisposable.DisposeAsync();
            }
            case IDisposable disposable:
            {
                disposable.Dispose();
                return ValueTask.CompletedTask;
            }
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Continues at the first incomplete or faulted result without invoking its disposer twice.
    /// Each consumed item is removed so completed resources are not kept alive by later awaits.
    /// </summary>
    private static async ValueTask AwaitRemainingAsync(ValueTask pending, List<object> resources, List<Exception>? failures)
    {
        while (true)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }

            if (resources.Count == 0) break;

            var index = resources.Count - 1;
            var resource = resources[index];
            resources.RemoveAt(index);

            try
            {
                pending = DisposeResource(resource);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
                pending = ValueTask.CompletedTask;
            }
        }

        if (failures is not null) throw new AggregateException(failures);
    }
}