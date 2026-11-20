using Everywhere.Modding.Hosting.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Common;

/// <summary>
/// Provides the process-lived service container to application composition and host-owned UI factories.
/// Mods must use <c>IModContext.Services</c> so collectible generic services belong to each activation's
/// <c>AssemblyLoadContext</c> rather than being pinned inside the host root container.
/// </summary>
internal static class ServiceLocator
{
    private static IServiceProvider? _serviceProvider;

    /// <summary>
    /// Builds application services once. <see cref="Entrance"/> owns asynchronous disposal.
    /// </summary>
    internal static HostServiceContainer Build(Action<ServiceCollection> configureServices)
    {
        if (_serviceProvider is not null)
        {
            throw new InvalidOperationException($"{nameof(ServiceLocator)} is already built.");
        }

        var serviceCollection = new ServiceCollection();
        configureServices(serviceCollection);
        var container = new HostServiceContainer(serviceCollection);
        _serviceProvider = container;
        return container;
    }

    /// <summary>
    /// Resolves a host-owned service. This method does not establish a collectible lifetime boundary.
    /// </summary>
    internal static object Resolve(Type type, object? key = null)
    {
        if (_serviceProvider is null)
        {
            throw new InvalidOperationException($"{nameof(ServiceLocator)} is not built.");
        }

        return key is null ? _serviceProvider.GetRequiredService(type) : _serviceProvider.GetRequiredKeyedService(type, key);
    }

    /// <summary>
    /// Resolves a typed host-owned service from the application container.
    /// </summary>
    internal static T Resolve<T>(object? key = null) where T : class => (T)Resolve(typeof(T), key);

    /// <summary>
    /// Disposes the built host service container on a thread-pool worker so service awaits do not capture a stopped UI dispatcher.
    /// </summary>
    internal static ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _serviceProvider, null) is not { } serviceProvider)
        {
            return ValueTask.CompletedTask;
        }

        // Enter disposal on a worker: ConfigureAwait(false) on callers cannot prevent a
        // service's own awaits from capturing Avalonia's stopped dispatcher context.
        return new ValueTask(Task.Run(async () =>
        {
            switch (serviceProvider)
            {
                case IAsyncDisposable asyncDisposable:
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                    break;
                case IDisposable disposable:
                    disposable.Dispose();
                    break;
            }
        }));
    }
}
