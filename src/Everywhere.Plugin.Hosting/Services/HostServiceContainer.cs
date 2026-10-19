using Autofac;
using Autofac.Builder;
using Autofac.Extensions.DependencyInjection;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Plugin.Hosting.Services;

/// <summary>
/// Owns application services and creates activation-local scopes for collectible plugin services.
/// Continue registering services through IServiceCollection; plugins use IPluginContext.Services.
/// </summary>
public sealed class HostServiceContainer : IKeyedServiceProvider, IDisposable, IAsyncDisposable, IPluginServiceBoundaryFactory
{
    private readonly IContainer _container;
    private readonly AutofacServiceProvider _provider;
    private readonly HostRegistrationSources _registrationSources;

    /// <summary>
    /// Builds the application container with scope-local native relationships and generic ownership
    /// filters. Activations bind collectible services and inherit host services through Autofac.
    /// </summary>
    public HostServiceContainer(IServiceCollection services)
    {
        var registrations = services.ToArray();
        _registrationSources = new HostRegistrationSources(registrations.Where(service => service.ServiceType.IsGenericTypeDefinition));

        var builder = new ContainerBuilder();
        builder.Populate(registrations.Where(service => !service.ServiceType.IsGenericTypeDefinition));
        _registrationSources.RegisterHost(builder);
        builder.RegisterInstance(this).As<IPluginServiceBoundaryFactory>().ExternallyOwned();
        _container = builder.Build(ContainerBuildOptions.ExcludeDefaultModules);
        _provider = new AutofacServiceProvider(_container);
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType) => _provider.GetService(serviceType);

    /// <inheritdoc />
    public object? GetKeyedService(Type serviceType, object? serviceKey) => _provider.GetKeyedService(serviceType, serviceKey);

    /// <inheritdoc />
    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => _provider.GetRequiredKeyedService(serviceType, serviceKey);

    /// <summary>
    /// Creates one scope per activation. Local Populate also owns the standard scope factory so
    /// IServiceScopeFactory cannot accidentally create a sibling under the application root.
    /// Plugin entries themselves remain lifecycle-owned and are not registered with the container.
    /// </summary>
    PluginServiceBoundary IPluginServiceBoundaryFactory.CreateBoundary(AssemblyLoadContext context)
    {
        var scope = _container.BeginLoadContextLifetimeScope(
            context,
            builder =>
            {
                _registrationSources.RegisterActivation(builder);
                builder.Populate([]);
                builder.Register(component => new PluginServiceBoundary(component.Resolve<ILifetimeScope>()))
                    .As<PluginServiceBoundary>()
                    .As<IServiceProvider>()
                    .As<IServiceScopeFactory>()
                    .As<IServiceProviderIsService>()
                    .As<IServiceProviderIsKeyedService>()
                    .As<IKeyedServiceProvider>()
                    .InstancePerLifetimeScope()
                    .ExternallyOwned();
            });

        return scope.Resolve<PluginServiceBoundary>();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _provider.Dispose();
        }
        finally
        {
            _registrationSources.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try
        {
            await _provider.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _registrationSources.Dispose();
        }
    }
}