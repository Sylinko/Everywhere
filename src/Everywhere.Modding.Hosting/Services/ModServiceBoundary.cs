using Autofac;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Modding.Hosting.Services;

/// <summary>
/// Owns an activation or child scope and presents only standard DI interfaces to mod code.
/// Native parent registrations preserve host identity and lifetimes. Collectible generic registrations
/// belong to the activation, and child scopes inherit that ownership through Autofac.
/// </summary>
internal sealed class ModServiceBoundary :
    ISupportRequiredService, IKeyedServiceProvider,
    IServiceProviderIsKeyedService, IServiceScopeFactory,
    IServiceScope, IAsyncDisposable
{
    /// <inheritdoc />
    public IServiceProvider ServiceProvider => this;

    private readonly ILifetimeScope _scope;
    private readonly AutofacServiceProvider _provider;

    /// <summary>
    /// Binds standard interface resolution to the scope without transferring application ownership.
    /// </summary>
    internal ModServiceBoundary(ILifetimeScope scope)
    {
        _scope = scope;
        _provider = new AutofacServiceProvider(scope);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType) => _provider.GetService(serviceType);

    /// <inheritdoc />
    public object GetRequiredService(Type serviceType) => _provider.GetRequiredService(serviceType);

    /// <inheritdoc />
    public object? GetKeyedService(Type serviceType, object? serviceKey) => _provider.GetKeyedService(serviceType, serviceKey);

    /// <inheritdoc />
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => _provider.GetRequiredKeyedService(serviceType, serviceKey);

    /// <inheritdoc />
    public bool IsService(Type serviceType) => _provider.IsService(serviceType);

    /// <inheritdoc />
    public bool IsKeyedService(Type serviceType, object? serviceKey) => _provider.IsKeyedService(serviceType, serviceKey);

    /// <inheritdoc />
    public IServiceScope CreateScope() => _scope.BeginLifetimeScope().Resolve<ModServiceBoundary>();

    /// <inheritdoc />
    public void Dispose() => _provider.Dispose();

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _provider.DisposeAsync();
}