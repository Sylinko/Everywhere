using Everywhere.Plugin.Hosting;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Hosting.Loading;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

/// <summary>
/// Exercises lifecycle and CLR collection using compiled DLLs that are never referenced by the test assembly.
/// </summary>
public sealed partial class PluginEngineTests
{
    private string _dataDirectory = "";

    /// <summary>
    /// Allocates data paths outside build outputs.
    /// </summary>
    [SetUp]
    public void SetUp()
    {
        _dataDirectory = Path.Combine(Path.GetTempPath(), "Everywhere.Plugin.Tests", Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// Removes the test-owned data after callbacks have completed.
    /// </summary>
    [TearDown]
    public void TearDown()
    {
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

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.CompletedWithErrors), Describe(result));
        Assert.That(result.ResidentAssemblyNames, Is.Empty);
        Assert.That(engine.Plugins.Single().State, Is.EqualTo(Abstractions.PluginState.Failed));
        Assert.That(engine.Plugins.Single().IsEnabled, Is.True);
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

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:fixture.b" }));
        services.Gate.SetResult();
        Assert.That((await engine.ApplyAsync([])).Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
    }

    /// <summary>
    /// A host root makes physical collection unconfirmed even after successful instance disposal.
    /// </summary>
    [Test]
    public async Task ApplyAsync_HostRetainsPlugin_ReportsRestartRequired()
    {
        var services = new Services { Mode = "retained" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("B")]));
        var result = await engine.ApplyAsync([]);
        services.Retained = null;

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
        Assert.That(result.Diagnostics.Select(item => item.Code), Does.Contain("CollectionUnconfirmed"));
        Assert.That(result.ActivePluginIds, Is.Empty);
        Assert.That(result.ResidentAssemblyNames, Does.Contain("PluginFixture.B"));
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

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
        Assert.That(result.ActivePluginIds, Is.Empty);
        Assert.That(result.ResidentAssemblyNames, Does.Contain("PluginFixture.B"));
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

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
        Assert.That(result.ResidentAssemblyNames, Does.Contain("PluginFixture.B"));
        Assert.That(services.Events, Is.EqualTo(new[] { "entry-dispose:fixture.b" }));
    }

    /// <summary>
    /// Creates the runtime against the framework and public contract only.
    /// </summary>
    private static PluginEngine CreateEngine(Services services, PluginEngineOptions? options = null)
    {
        return new PluginEngine(
            new HostAssemblyCatalog([]),
            services,
            options ?? new PluginEngineOptions
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
        File.Copy(Path.Combine(fixture.Installation.Directory, "PluginFixture.B.dll"), Path.Combine(directory, "PluginFixture.B.dll"));
        File.Copy(
            Path.Combine(fixture.Installation.Directory, "PluginFixture.B.resources.dll"),
            Path.Combine(directory, "assets", "translation.dll"));
        var manifest = fixture.Installation.Manifest with
        {
            Entry = fixture.Entry with { Assemblies = ["PluginFixture.B.dll", "assets/translation.dll"] }
        };
        var definition = new PluginDefinition(
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
    private PluginDefinition Definition(string suffix, string? pluginId = null)
    {
        var assembly = $"PluginFixture.{suffix}.dll";
        var id = pluginId ?? "fixture." + suffix.ToLowerInvariant();
        var manifest = new PluginManifest
        {
            Id = id,
            Version = "1.0.0",
            Entry = new UniversalPluginEntry
            {
                Assembly = assembly,
                Type = "PluginFixture.Entry",
                Framework = "net10.0",
                PluginApiVersion = "[1.0,2.0)",
                Assemblies = [assembly],
                Dependencies = suffix == "A" ?
                    [new PluginDependency { Id = "fixture.b", Version = "[1.0,2.0)", IsOptional = true, Assemblies = ["PluginFixture.B"] }] :
                    []
            }
        };

        return new PluginDefinition(
            new PluginInstallation
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
    private static void AssertApplied(PluginApplyResult result)
    {
        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.Applied), Describe(result));
    }

    /// <summary>
    /// Formats host-owned diagnostics without holding collectible exceptions.
    /// </summary>
    private static string Describe(PluginApplyResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Code + ": " + item.Detail));
    }

    /// <summary>
    /// Supplies only framework-owned callbacks and values to the collectible fixture.
    /// </summary>
    private sealed class Services : IServiceProvider
    {
        /// <summary>
        /// Gets callback observations in invocation order.
        /// </summary>
        internal List<string> Events { get; } = [];

        /// <summary>
        /// Observes only host-owned lifecycle values from the collectible fixture.
        /// </summary>
        internal Action<Abstractions.PluginStopReason?>? ObserveStopReason { get; init; }

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

        /// <inheritdoc />
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(Action<string>)) return (Action<string>)Log;
            if (serviceType == typeof(Action<Abstractions.PluginStopReason?>)) return ObserveStopReason;
            if (serviceType == typeof(string)) return Mode;
            if (serviceType == typeof(Func<string, string>)) return Modes;
            if (serviceType == typeof(TaskCompletionSource)) return Gate;
            if (serviceType == typeof(Action<object>)) return (Action<object>)(value => Retained = value);

            return null;
        }

        /// <summary>
        /// Records independent exit callbacks without concurrent writes to the observation list.
        /// </summary>
        private void Log(string message)
        {
            lock (Events)
            {
                Events.Add(message);
            }

            if (message.StartsWith("initialize:", StringComparison.Ordinal)) Initialized.TrySetResult();
        }
    }
}