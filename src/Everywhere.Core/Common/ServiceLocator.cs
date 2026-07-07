using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Common;

public static class ServiceLocator
{
    private static IServiceProvider? _serviceProvider;

    public static void SetProvider(IServiceProvider serviceProvider)
    {
        if (_serviceProvider != null) throw new InvalidOperationException($"{nameof(ServiceLocator)} is already built.");
        _serviceProvider = serviceProvider;
    }

    public static object Resolve(Type type, object? key = null)
    {
        if (_serviceProvider == null) throw new InvalidOperationException($"{nameof(ServiceLocator)} is not built.");
        if (key != null) throw new NotSupportedException("Keyed service resolution is not supported by the source-generated provider.");
        if (key == null) return _serviceProvider.GetRequiredService(type);
        throw new InvalidOperationException("Unreachable service resolution branch.");
    }

    public static T Resolve<T>(object? key = null) where T : class
    {
        return (T)Resolve(typeof(T), key);
    }
}
