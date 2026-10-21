using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Hosting.Services;
using Everywhere.Modding.Packaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Modding.Tests;

/// <summary>
/// Exercises lifecycle and CLR collection using compiled DLLs that are never referenced by the test assembly.
/// </summary>
public sealed partial class ModEngineTests
{
    private string _dataDirectory = "";
    private readonly List<HostServiceContainer> _containers = [];

    /// <summary>
    /// Allocates data paths outside build outputs.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "Everywhere.Modding.Tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Removes the test-owned data after callbacks have completed.
    /// </summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        _containers.Clear();
        if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
    }

    /// <summary>
    /// Changing optional availability reconstructs the consumer and releases every old owner.
    /// </summary>
    [Test]
    public async Task ApplyAsync_OptionalDependencyToggled_ReconstructsConsumerAndCollects()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");

        AssertApplied(await engine.ApplyAsync([a]));
        Assert.That(services.Events, Does.Contain("optional:1"));
        services.Events.Clear();

        AssertApplied(await engine.ApplyAsync([a, b]));
        Assert.That(
            services.Events,
            Is.EqualTo(
                new[]
                {
                    "entry-dispose:fixture.a", "dispose:second:fixture.a", "dispose:first:fixture.a",
                    "initialize:fixture.b", "initialize:fixture.a", "optional:42"
                }));
        services.Events.Clear();

        AssertApplied(await engine.ApplyAsync([a]));
        Assert.That(services.Events.IndexOf("entry-dispose:fixture.a"), Is.LessThan(services.Events.IndexOf("entry-dispose:fixture.b")));
        Assert.That(services.Events, Does.Contain("optional:1"));

        var stopped = await engine.ApplyAsync([]);
        AssertApplied(stopped);
        Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
    }

    /// <summary>
    /// Partial initialization runs entry disposal and reverse cleanup, then permits an explicit corrected retry.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InitializationFails_CleansUpAndAllowsRetry()
    {
        var services = new Services { Mode = "initialize-failure" };
        var engine = CreateEngine(services);
        var result = await engine.ApplyAsync([Definition("B")]);

        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ResidentAssemblyNames, Is.Empty);
        Assert.That(engine.Mods.Single().State, Is.EqualTo(ModState.Failed));
        Assert.That(engine.Mods.Single().IsEnabled, Is.True);
        Assert.That(
            services.Events,
            Is.EqualTo(
                new[]
                {
                    "initialize:fixture.b", "entry-dispose:fixture.b", "dispose:second:fixture.b", "dispose:first:fixture.b"
                }));

        services.Mode = "normal";
        AssertApplied(await engine.ApplyAsync([Definition("B")]));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// A still-running initializer must not race registered resource disposal.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InitializationTimesOut_RequiresRestartWithoutConcurrentCleanup()
    {
        var services = new Services { Mode = "initialize-timeout" };
        var engine = CreateEngine(services);
        var result = await engine.ApplyAsync([Definition("B")]);

        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.RestartRequired));
        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:fixture.b" }));
        services.Gate.SetResult();
        Assert.That((await engine.ApplyAsync([])).Status, Is.EqualTo(ModApplyStatus.RestartRequired));
    }

    /// <summary>
    /// A host root makes physical collection unconfirmed even after successful instance disposal.
    /// </summary>
    [Test]
    public async Task ApplyAsync_HostRetainsMod_ReportsRestartRequired()
    {
        var services = new Services { Mode = "retained" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("B")]));
        var result = await engine.ApplyAsync([]);
        services.Retained = null;

        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.RestartRequired));
        Assert.That(result.Diagnostics.Select(item => item.Code), Does.Contain("CollectionUnconfirmed"));
        Assert.That(result.ActiveModIds, Is.Empty);
        Assert.That(result.ResidentAssemblyNames, Does.Contain("ModFixture.B"));
    }

    /// <summary>
    /// A failed disposer cannot prevent the remaining disposers from running, but it blocks collection claims.
    /// </summary>
    [Test]
    public async Task ApplyAsync_CleanupFails_DisposesRemainingResourcesAndRequiresRestart()
    {
        var services = new Services { Mode = "cleanup-failure" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("B")]));
        services.Events.Clear();
        var result = await engine.ApplyAsync([]);

        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.RestartRequired));
        Assert.That(result.ActiveModIds, Is.Empty);
        Assert.That(result.ResidentAssemblyNames, Does.Contain("ModFixture.B"));
        Assert.That(
            services.Events,
            Is.EqualTo(
                new[]
                {
                    "entry-dispose:fixture.b", "dispose:second:fixture.b", "dispose:first:fixture.b"
                }));
    }

    /// <summary>
    /// A throwing entry disposer cannot be treated as proof that background users have stopped.
    /// </summary>
    [Test]
    public async Task ApplyAsync_EntryDisposalFails_RetainsResourcesAndRequiresRestart()
    {
        var services = new Services { Mode = "disposal-failure" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("B")]));
        services.Events.Clear();
        var result = await engine.ApplyAsync([]);

        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.RestartRequired));
        Assert.That(result.ResidentAssemblyNames, Does.Contain("ModFixture.B"));
        Assert.That(services.Events, Is.EqualTo(new[] { "entry-dispose:fixture.b" }));
    }

    /// <summary>
    /// Creates the runtime against the framework and public contract only.
    /// </summary>
    private ModEngine CreateEngine(Services services, ModEngineOptions? options = null)
    {
        var registrations = new ServiceCollection();
        registrations.AddSingleton<Action<string>>(_ => message => services.Record(message));
        registrations.AddTransient(_ => services.Mode);
        registrations.AddSingleton(services.Gate);
        if (services.InspectServices is { } inspect)
        {
            registrations.AddSingleton(inspect);
        }

        if (services.ObserveStopReason is { } observeStopReason)
        {
            registrations.AddSingleton(observeStopReason);
        }

        services.ConfigureServices?.Invoke(registrations);
        registrations.AddSingleton<Action<object>>(_ => value => services.Retained = value);
        if (services.Modes is { } modes)
        {
            registrations.AddSingleton(modes);
        }

        var container = new HostServiceContainer(registrations);
        _containers.Add(container);
        return new ModEngine(
            new HostAssemblyCatalog([typeof(ILogger<>).Assembly.Location]),
            container,
            options ?? new ModEngineOptions
            {
                CallbackTimeout = TimeSpan.FromMilliseconds(300),
                CollectionAttempts = 12,
                CollectionDelay = TimeSpan.FromMilliseconds(10)
            });
    }

    /// <summary>
    /// Resource lookup uses PE identity and culture even when the package path is unrelated to language names.
    /// Missing specific and unrelated cultures use ResourceManager's standard fallback; the owner still collects.
    /// </summary>
    [Test]
    public async Task ApplyAsync_SatelliteAtDeclaredOpaquePath_ResolvesCultureFallbackAndCollects()
    {
        var directory = Path.Combine(_dataDirectory, "package");
        Directory.CreateDirectory(Path.Combine(directory, "assets"));
        var fixture = Definition("B");
        File.Copy(Path.Combine(fixture.Installation.Directory, "ModFixture.B.dll"), Path.Combine(directory, "ModFixture.B.dll"));
        File.Copy(
            Path.Combine(fixture.Installation.Directory, "ModFixture.B.resources.dll"),
            Path.Combine(directory, "assets", "translation.dll"));
        var manifest = fixture.Installation.Manifest with
        {
            Entry = fixture.Entry with { Assemblies = ["ModFixture.B.dll", "assets/translation.dll"] }
        };
        var definition = new ModDefinition(
            fixture.Installation with { Directory = directory, Manifest = manifest },
            Path.Combine(_dataDirectory, "data"),
            "win-x64",
            "1.0.0",
            "1.0.0",
            "net10.0");
        var services = new Services { Mode = "resources" };
        var engine = CreateEngine(services);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            services.Events.Clear();
            AssertApplied(await engine.ApplyAsync([definition]));
            Assert.That(services.Events, Does.Contain("resource:French"));
            Assert.That(services.Events, Does.Contain("resource:Neutral"));
            var stopped = await engine.ApplyAsync([]);
            AssertApplied(stopped);
            Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
        }
    }

    /// <summary>
    /// Selects a fixture with explicit ownership and optional B metadata.
    /// </summary>
    private ModDefinition Definition(string suffix, string? modId = null)
    {
        var assembly = $"ModFixture.{suffix}.dll";
        var id = modId ?? "fixture." + suffix.ToLowerInvariant();
        var manifest = new ModManifest
        {
            Id = id,
            Version = "1.0.0",
            Entry = new UniversalModEntry
            {
                Assembly = assembly,
                Type = "ModFixture.Entry",
                Framework = "net10.0",
                ModApiVersion = "[1.0,2.0)",
                Assemblies = [assembly],
                Dependencies = suffix == "A" ?
                    [new ModDependency { Id = "fixture.b", Version = "[1.0,2.0)", IsOptional = true, Assemblies = ["ModFixture.B"] }] :
                    []
            }
        };

        return new ModDefinition(
            new ModInstallation
            {
                InstallationId = suffix,
                Directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures"),
                Manifest = manifest
            },
            Path.Combine(_dataDirectory, id),
            "win-x64",
            "1.0.0",
            "1.0.0",
            "net10.0");
    }

    /// <summary>
    /// Includes runtime diagnostics when an expected transition fails.
    /// </summary>
    private static void AssertApplied(ModApplyResult result)
    {
        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(result));
    }

    /// <summary>
    /// Formats host-owned diagnostics without holding collectible exceptions.
    /// </summary>
    private static string Describe(ModApplyResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Code + ": " + item.Detail));
    }

    /// <summary>
    /// Supplies only framework-owned callbacks and values to the collectible fixture.
    /// </summary>
    private sealed class Services
    {
        /// <summary>
        /// Gets callback observations in invocation order.
        /// </summary>
        internal List<string> Events { get; } = [];

        /// <summary>
        /// Exercises the provider with the actual collectible entry type without retaining reflection objects.
        /// </summary>
        internal Action<IServiceProvider, Type>? InspectServices { get; init; }

        /// <summary>
        /// Adds host registrations used by service-boundary integration scenarios.
        /// </summary>
        internal Action<IServiceCollection>? ConfigureServices { get; init; }

        /// <summary>
        /// Observes only host-owned lifecycle values from the collectible fixture.
        /// </summary>
        internal Action<ModStopReason?>? ObserveStopReason { get; init; }

        /// <summary>
        /// Gets or sets the next injected lifecycle behavior.
        /// </summary>
        internal string Mode { get; set; } = "normal";

        /// <summary>
        /// Gets or sets behavior for an individual activation instead of the default shared mode.
        /// </summary>
        internal Func<string, string>? Modes { get; init; }

        /// <summary>
        /// Signals that a callback has entered the fixture before a test races exit with initialization.
        /// </summary>
        internal TaskCompletionSource Initialized { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets a gate for completing a deliberately timed-out callback.
        /// </summary>
        internal TaskCompletionSource Gate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Gets or sets the deliberate retention control.
        /// </summary>
        internal object? Retained { get; set; }

        /// <summary>
        /// Records independent exit callbacks without concurrent writes to the observation list.
        /// </summary>
        internal void Record(string message)
        {
            lock (Events)
            {
                Events.Add(message);
            }

            if (message.StartsWith("initialize:", StringComparison.Ordinal)) Initialized.TrySetResult();
        }
    }
}