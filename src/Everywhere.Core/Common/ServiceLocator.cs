using Everywhere.Modding.Hosting.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Common;

/// <summary>
/// Provides the process-lived container to application composition and host-owned UI factories.
/// Plugin code must use IModContext.Services so collectible generic services belong to its activation.
/// Do not pass this provider to plugin factories or resolve services closed over plugin types here.
/// </summary>
internal static class ServiceLocator
{
    private static HostServiceContainer? _serviceProvider;

    /// <summary>
    /// Builds application services once. The platform entry point owns asynchronous disposal.
    /// </summary>
    internal static HostServiceContainer Build(Action<ServiceCollection> configureServices)
    {
        if (_serviceProvider is not null)
        {
            throw new InvalidOperationException($"{nameof(ServiceLocator)} is already built.");
        }

        var serviceCollection = new ServiceCollection();
        configureServices(serviceCollection);
        return _serviceProvider = new HostServiceContainer(serviceCollection);
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
    internal static T Resolve<T>(object? key = null) where T : class
    {
        return (T)Resolve(typeof(T), key);
    }
}