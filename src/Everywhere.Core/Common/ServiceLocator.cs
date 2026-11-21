using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Common;

public static class ServiceLocator
{
    private static ServiceProvider? _serviceProvider;

    public static ServiceProvider Build(Action<ServiceCollection> configureServices)
    {
        if (_serviceProvider != null) throw new InvalidOperationException($"{nameof(ServiceLocator)} is already built.");
        var serviceCollection = new ServiceCollection();
        configureServices(serviceCollection);
        return _serviceProvider = serviceCollection.BuildServiceProvider();
    }

    public static object Resolve(Type type, object? key = null)
    {
        if (_serviceProvider == null) throw new InvalidOperationException($"{nameof(ServiceLocator)} is not built.");
        if (key == null) return _serviceProvider.GetRequiredService(type);
        return _serviceProvider.GetRequiredKeyedService(type, key);
    }

    public static T Resolve<T>(object? key = null) where T : class
    {
        return (T)Resolve(typeof(T), key);
    }

    internal static ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _serviceProvider, null) is not { } serviceProvider)
        {
            return ValueTask.CompletedTask;
        }

        // Enter disposal on a worker: ConfigureAwait(false) on callers cannot prevent a
        // service's own awaits from capturing Avalonia's stopped dispatcher context.
        return new ValueTask(Task.Run(async () => await serviceProvider.DisposeAsync().ConfigureAwait(false)));
    }
}