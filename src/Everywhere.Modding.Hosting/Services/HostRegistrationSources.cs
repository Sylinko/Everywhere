using Autofac;
using Autofac.Builder;
using Autofac.Core;
using Autofac.Core.Registration;
using Autofac.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using ZLinq;

namespace Everywhere.Modding.Hosting.Services;

/// <summary>
/// Uses Autofac's native registration sources while keeping collectible requests out of the root.
/// The template supplies descriptor translation and built-in relationships without activating services.
/// </summary>
internal sealed class HostRegistrationSources : IDisposable
{
    private readonly IContainer _defaults;
    private readonly IContainer _generics;
    private readonly IRegistrationSource[] _genericSources;
    private readonly IRegistrationSource[] _defaultSources;

    /// <summary>
    /// Separates native relationships from descriptor sources so activations bind collectible
    /// generics without duplicating host services. Neither source container activates services.
    /// </summary>
    internal HostRegistrationSources(IEnumerable<ServiceDescriptor> registrations)
    {
        _defaults = new ContainerBuilder().Build();
        _defaultSources = _defaults.ComponentRegistry.Sources.AsValueEnumerable().Reverse().ToArray();

        var builder = new ContainerBuilder();
        builder.Populate(registrations);
        _generics = builder.Build(ContainerBuildOptions.ExcludeDefaultModules);
        _genericSources = _generics.ComponentRegistry.Sources.AsValueEnumerable().Reverse().ToArray();
    }

    /// <summary>
    /// Makes native relationships scope-local and keeps collectible generic bindings out of the root.
    /// Per-scope sources are copied by Autofac and skipped when an isolated child queries its parent.
    /// </summary>
    internal void RegisterHost(ContainerBuilder builder)
    {
        foreach (var source in _defaultSources)
        {
            builder.RegisterSource(new ScopeSource(source));
        }

        foreach (var source in _genericSources)
        {
            builder.RegisterSource(new OwnedSource(source, ownsCollectibleTypes: false));
        }
    }

    /// <summary>
    /// Installs collectible generic sources locally. Native relationships are inherited through
    /// Autofac's normal source ordering; host generic services continue through the parent registry.
    /// </summary>
    internal void RegisterActivation(ContainerBuilder builder)
    {
        foreach (var source in _genericSources)
        {
            builder.RegisterSource(new OwnedSource(source, ownsCollectibleTypes: true));
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _generics.Dispose();
        _defaults.Dispose();
    }

    /// <summary>
    /// Uses Autofac's per-scope extension point to prevent relationship sources from making nested
    /// parent queries that lose the isolation marker. Preserve each source's native adapter flag
    /// so inherited explicit registrations still take precedence over generated relationships.
    /// </summary>
    private sealed class ScopeSource(IRegistrationSource source) : IRegistrationSource, IPerScopeRegistrationSource
    {
        /// <inheritdoc />
        public bool IsAdapterForIndividualComponents => source.IsAdapterForIndividualComponents;

        /// <inheritdoc />
        public IEnumerable<IComponentRegistration> RegistrationsFor(
            Service service,
            Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
        {
            return source.RegistrationsFor(service, registrationAccessor);
        }
    }

    /// <summary>
    /// Assigns native generic binding to the registry that owns its type arguments. The wrapper is
    /// not an adapter: descendants inherit registrations through their parent, rather than copying
    /// a root or activation ownership rule into another registry.
    /// </summary>
    private sealed class OwnedSource(IRegistrationSource source, bool ownsCollectibleTypes) : IRegistrationSource
    {
        /// <inheritdoc />
        public bool IsAdapterForIndividualComponents => false;

        /// <inheritdoc />
        public IEnumerable<IComponentRegistration> RegistrationsFor(
            Service service,
            Func<Service, IEnumerable<ServiceRegistration>> registrationAccessor)
        {
            if (service is not IServiceWithType typedService || typedService.ServiceType.IsCollectible != ownsCollectibleTypes)
            {
                return [];
            }

            return source.RegistrationsFor(service, registrationAccessor);
        }
    }
}