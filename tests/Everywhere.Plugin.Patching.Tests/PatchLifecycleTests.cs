using System.Reflection;
using System.Runtime.CompilerServices;
using Everywhere.Plugin.Hosting;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Hosting.Loading;
using Everywhere.Plugin.Packaging;
using NUnit.Framework;

namespace Everywhere.Plugin.Patching.Tests;

/// <summary>
/// Checks real hooks against the current engine without making patching a host or SDK dependency.
/// </summary>
[NonParallelizable]
public sealed class PatchLifecycleTests
{
    /// <summary>
    /// Removing either owner collects it while leaving the other callback usable.
    /// </summary>
    [TestCase("A")]
    [TestCase("B")]
    public async Task ApplyAsync_TwoHooks_RemovesEachOwnerIndependently(string removed)
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");
        var remaining = removed == "A" ? b : a;

        AssertApplied(await engine.ApplyAsync([a, b]));
        Assert.That(Target("item"), Is.EqualTo("itemBA"));
        var reduced = await engine.ApplyAsync([remaining]);
        AssertApplied(reduced);
        Assert.That(reduced.ResidentAssemblyNames, Does.Not.Contain("PluginFixture.Patch" + removed));
        Assert.That(Target("item"), Is.EqualTo(removed == "A" ? "itemB" : "itemA"));
        AssertApplied(await engine.ApplyAsync([a, b]));
        Assert.That(Target("item"), Is.EqualTo("itemBA"));
        var stopped = await engine.ApplyAsync([]);
        AssertApplied(stopped);
        Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
        Assert.That(Target("item"), Is.EqualTo("item"));
    }

    /// <summary>
    /// A failed initializer must undo an already effective hook and permit a corrected retry.
    /// </summary>
    [Test]
    public async Task ApplyAsync_InitializationFailsAfterHook_RestoresChainAndAllowsRetry()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = Definition("A");
        var b = Definition("B");
        AssertApplied(await engine.ApplyAsync([a]));
        services.FailingPluginId = "patch.b";
        var failed = await engine.ApplyAsync([a, b]);

        Assert.That(services.Observations, Does.Contain("itemBA"));
        Assert.That(failed.Status, Is.EqualTo(PluginApplyStatus.ActivationFailed), Describe(failed));
        Assert.That(failed.ResidentAssemblyNames, Does.Not.Contain("PluginFixture.PatchB"));
        Assert.That(Target("item"), Is.EqualTo("itemA"));

        services.FailingPluginId = null;
        AssertApplied(await engine.ApplyAsync([a, b]));
        Assert.That(Target("item"), Is.EqualTo("itemBA"));
        var stopped = await engine.ApplyAsync([]);
        AssertApplied(stopped);
        Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
        Assert.That(Target("item"), Is.EqualTo("item"));
    }

    /// <summary>
    /// Exit leaves hooks installed, but entry disposal makes later host calls pass through safely.
    /// The dedicated target isolates this deliberately retained exit-only chain from hot-unload tests.
    /// </summary>
    [Test]
    public async Task DisposeAsync_InstalledHooks_DisposesEntriesWithoutUndoingHooks()
    {
        var services = new Services { IsExitTarget = true };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("A"), Definition("B")]));
        Assert.That(ExitTarget("item"), Is.EqualTo("itemBA"));

        await engine.DisposeAsync();

        Assert.That(ExitTarget("item"), Is.EqualTo("item"));
        Assert.That(services.Observations, Does.Contain("stopped:patch.a"));
        Assert.That(services.Observations, Does.Contain("stopped:patch.b"));
    }

    /// <summary>
    /// Provides a separate target for hooks intentionally retained until this test process exits.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ExitTarget(string value) => value;

    /// <summary>
    /// Provides an ordinary host method with a stable, non-inlined call site.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string Target(string value) => value;

    /// <summary>
    /// Shares the test host's managed dependencies, including RuntimeDetour, through Default.
    /// </summary>
    private static PluginEngine CreateEngine(Services services)
    {
        var paths = Directory.GetFiles(TestContext.CurrentContext.TestDirectory, "*.dll");
        return new PluginEngine(new HostAssemblyCatalog(paths), services, new PluginEngineOptions
        {
            CollectionAttempts = 30,
            CollectionDelay = TimeSpan.FromMilliseconds(10)
        });
    }

    /// <summary>
    /// Selects one fixture without declaring a dependency on the other patch owner.
    /// </summary>
    private static PluginDefinition Definition(string suffix)
    {
        var assembly = $"PluginFixture.Patch{suffix}.dll";
        return new PluginDefinition(new PluginInstallation
        {
            InstallationId = suffix,
            Directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures"),
            Manifest = new PluginManifest
            {
                Id = "patch." + suffix.ToLowerInvariant(),
                Version = "1.0.0",
                Entry = new UniversalPluginEntry
                {
                    Assembly = assembly,
                    Type = "PluginFixture.Patching.Entry",
                    Framework = "net10.0",
                    PluginApiVersion = "[1.0,2.0)",
                    Assemblies = [assembly]
                }
            }
        }, Path.GetTempPath(), "win-x64", "1.0.0", "1.0.0", "net10.0");
    }

    /// <summary>
    /// Requires confirmed cleanup instead of accepting a restart fallback as successful removal.
    /// </summary>
    private static void AssertApplied(PluginApplyResult result)
    {
        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.Applied), Describe(result));
    }

    /// <summary>
    /// Formats diagnostics without retaining collectible exception objects.
    /// </summary>
    private static string Describe(PluginApplyResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Code + ": " + item.Detail));
    }

    /// <summary>
    /// Supplies framework-owned observations and a host method to both collectible plugins.
    /// </summary>
    private sealed class Services : IServiceProvider
    {
        /// <summary>
        /// Gets whether this test uses the independently retained process-exit target.
        /// </summary>
        internal bool IsExitTarget { get; init; }

        /// <summary>
        /// Gets or sets the plugin whose initialization should fail after its hook becomes effective.
        /// </summary>
        internal string? FailingPluginId { get; set; }

        /// <summary>
        /// Gets results observed immediately after hook installation.
        /// </summary>
        internal List<string> Observations { get; } = [];

        /// <inheritdoc />
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(MethodInfo)) return typeof(PatchLifecycleTests).GetMethod(IsExitTarget ? nameof(ExitTarget) : nameof(Target));
            if (serviceType == typeof(string)) return FailingPluginId;
            if (serviceType == typeof(Action)) return (Action)(() => Observations.Add(IsExitTarget ? ExitTarget("item") : Target("item")));
            if (serviceType == typeof(Action<string>)) return (Action<string>)Observations.Add;

            return null;
        }
    }
}