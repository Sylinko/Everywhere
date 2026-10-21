using Everywhere.Modding.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Modding.Tests;

public sealed partial class ModEngineTests
{
    /// <summary>
    /// Real loggers, lazy relationships and keyed lookups must collect while another mod and
    /// the application container remain alive.
    /// </summary>
    [TestCase("direct")]
    [TestCase("lazy")]
    [TestCase("keyed")]
    public async Task ApplyAsync_ModLogger_UnloadsIndependently(string resolution)
    {
        var services = new Services
        {
            ConfigureServices = registrations => registrations.AddLogging()
                .AddKeyedSingleton(typeof(ILogger<>), "logger", typeof(Logger<>)),
            InspectServices = (provider, entryType) =>
            {
                var loggerType = typeof(ILogger<>).MakeGenericType(entryType);
                var availability = provider.GetRequiredService<IServiceProviderIsService>();
                Assert.That(availability.IsService(loggerType), Is.True);
                Assert.That(availability.IsService(entryType), Is.False);

                var serviceType = resolution == "lazy" ? typeof(Lazy<>).MakeGenericType(loggerType) : loggerType;
                Assert.That(availability.IsService(serviceType), Is.True);
                if (resolution == "keyed")
                {
                    var keyedAvailability = provider.GetRequiredService<IServiceProviderIsKeyedService>();
                    Assert.That(keyedAvailability.IsKeyedService(loggerType, "logger"), Is.True);
                    Assert.That(keyedAvailability.IsKeyedService(loggerType, "missing"), Is.False);
                }

                var service = resolution == "keyed"
                    ? provider.GetRequiredKeyedService(serviceType, "logger")
                    : provider.GetRequiredService(serviceType);
                var logger = resolution == "lazy"
                    ? (ILogger)(serviceType.GetProperty("Value")?.GetValue(service)
                        ?? throw new AssertionException("Missing lazy logger."))
                    : (ILogger)service;

                logger.LogInformation("Collectible mod invocation");
            }
        };
        var engine = CreateEngine(services);
        var first = Definition("B");
        var second = ResourceDefinition("services");

        AssertApplied(await engine.ApplyAsync([first, second]));
        AssertApplied(await engine.ApplyAsync([second]));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// Direct, collection and child-scope resolution share host singletons, preserve scoped and
    /// transient lifetimes, and dispose collectible generic services with their activation.
    /// </summary>
    [Test]
    public async Task ApplyAsync_GenericServices_PreserveHostOwnershipAndScopeLifetimes()
    {
        var marker = new ServiceMarker();
        var services = new Services
        {
            ConfigureServices = registrations => registrations
                .AddSingleton(_ => marker)
                .AddSingleton(typeof(ICategory<>), typeof(Category<>))
                .AddScoped<ScopedMarker>()
                .AddScoped(typeof(ScopedCategory<>))
                .AddTransient(typeof(TransientCategory<>)),
            InspectServices = (provider, entryType) => InspectServices(provider, entryType, marker)
        };
        var engine = CreateEngine(services);
        var first = Definition("B");
        var second = ResourceDefinition("services");

        for (var cycle = 0; cycle < 2; cycle++)
        {
            AssertApplied(await engine.ApplyAsync([first, second]));
            AssertApplied(await engine.ApplyAsync([second]));
            Assert.That(marker.DisposedCategories, Does.Contain("ModFixture.B"));
            Assert.That(marker.DisposedCategories, Does.Not.Contain("ModFixture.Resources"));
            Assert.That(marker.IsDisposed, Is.False);

            AssertApplied(await engine.ApplyAsync([]));
            Assert.That(marker.DisposedCategories, Does.Contain("ModFixture.Resources"));
            Assert.That(marker.IsDisposed, Is.False);
            marker.DisposedCategories.Clear();
        }

        await _containers.Single().DisposeAsync();
        Assert.That(marker.IsDisposed, Is.True);
    }

    /// <summary>
    /// Failing initialization still disposes the mod's generic services before collection.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InitializationFailsAfterGenericResolution_DisposesScopeAndCollects()
    {
        var marker = new ServiceMarker();
        var services = new Services
        {
            Mode = "initialize-failure",
            ConfigureServices = registrations => registrations
                .AddSingleton(_ => marker)
                .AddSingleton(typeof(ICategory<>), typeof(Category<>)),
            InspectServices = (provider, entryType) => provider.GetRequiredService(typeof(ICategory<>).MakeGenericType(entryType))
        };
        var engine = CreateEngine(services);

        var result = await engine.ApplyAsync([Definition("B")]);

        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ResidentAssemblyNames, Is.Empty);
        Assert.That(marker.DisposedCategories, Is.EqualTo(new[] { "ModFixture.B" }));
        Assert.That(marker.IsDisposed, Is.False);
    }

    /// <summary>
    /// Checks service identity while the collectible type is confined to an initialization callback.
    /// </summary>
    private static void InspectServices(IServiceProvider provider, Type entryType, ServiceMarker marker)
    {
        var categoryType = typeof(ICategory<>).MakeGenericType(entryType);
        var category = provider.GetRequiredService(categoryType);
        Assert.That(category.GetType().GetProperty("Services")?.GetValue(category), Is.SameAs(provider));
        Assert.That(provider.GetRequiredService<ServiceMarker>(), Is.SameAs(marker));
        Assert.That(provider.GetRequiredService<IServiceProvider>(), Is.SameAs(provider));

        var host = provider.GetRequiredService<ICategory<ServiceMarker>>();
        Assert.That(provider.GetRequiredService<IEnumerable<ICategory<ServiceMarker>>>().Single(), Is.SameAs(host));
        var scoped = provider.GetRequiredService<ScopedMarker>();
        var scopedGeneric = provider.GetRequiredService<ScopedCategory<ServiceMarker>>();
        using var child = provider.CreateScope();
        Assert.That(child.ServiceProvider.GetRequiredService<ServiceMarker>(), Is.SameAs(marker));
        Assert.That(child.ServiceProvider.GetRequiredService<ICategory<ServiceMarker>>(), Is.SameAs(host));
        Assert.That(child.ServiceProvider.GetRequiredService<ScopedMarker>(), Is.Not.SameAs(scoped));
        Assert.That(child.ServiceProvider.GetRequiredService<ScopedCategory<ServiceMarker>>(), Is.Not.SameAs(scopedGeneric));
        Assert.That(child.ServiceProvider.GetRequiredService(categoryType), Is.SameAs(category));
        Assert.That(provider.GetRequiredService<TransientCategory<ServiceMarker>>(),
            Is.Not.SameAs(provider.GetRequiredService<TransientCategory<ServiceMarker>>()));
        Assert.That(provider.GetRequiredService(typeof(IEnumerable<>).MakeGenericType(categoryType)), Is.Not.Null);
    }

    /// <summary>
    /// Supplies a host-defined generic contract closed over mod-owned arguments.
    /// </summary>
    public interface ICategory<T>;

    /// <summary>
    /// Records which generic singleton is disposed without retaining mod types in observations.
    /// Both disposal interfaces ensure asynchronous retirement invokes exactly one path.
    /// </summary>
    public sealed class Category<T>(ServiceMarker marker, IServiceProvider services) : ICategory<T>, IDisposable, IAsyncDisposable
    {
        /// <summary>
        /// Captures the service provider selected by constructor injection inside this scope.
        /// </summary>
        public IServiceProvider Services { get; } = services;

        /// <inheritdoc />
        public void Dispose() => throw new InvalidOperationException("Asynchronous scope disposal must prefer IAsyncDisposable.");

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            marker.DisposedCategories.Add(typeof(T).Assembly.GetName().Name ?? "");
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Observes application singleton lifetime independently of mod-owned services.
    /// </summary>
    public sealed class ServiceMarker : IDisposable
    {
        /// <summary>
        /// Gets whether application ownership has ended.
        /// </summary>
        public bool IsDisposed { get; private set; }

        /// <summary>
        /// Gets value-only generic disposal observations.
        /// </summary>
        public List<string> DisposedCategories { get; } = [];

        /// <inheritdoc />
        public void Dispose() => IsDisposed = true;
    }

    /// <summary>
    /// Identifies standard scoped service instances.
    /// </summary>
    public sealed class ScopedMarker;

    /// <summary>
    /// Identifies scoped services whose generic arguments are host-owned.
    /// </summary>
    public sealed class ScopedCategory<T>;

    /// <summary>
    /// Identifies transient services whose generic arguments are host-owned.
    /// </summary>
    public sealed class TransientCategory<T>;
}
