using System.Reflection;
using System.Runtime.CompilerServices;
using Everywhere.Modding.Hosting;
using Everywhere.Modding.Hosting.Services;
using Microsoft.Extensions.DependencyInjection;
using Everywhere.Modding.Hosting.Installation;
using Everywhere.Modding.Hosting.Loading;
using Everywhere.Modding.Packaging;
using NUnit.Framework;

namespace Everywhere.Modding.Patching.Tests;

/// <summary>
/// Checks real hooks against the current engine without making patching a host or SDK dependency.
/// </summary>
[NonParallelizable]
public sealed class PatchLifecycleTests
{
    private readonly List<HostServiceContainer> _containers = [];

    /// <summary>
    /// Releases each test root after observing collection with the root still alive.
    /// </summary>
    [TearDown]
    public async Task TearDown()
    {
        foreach (var container in _containers)
        {
            await container.DisposeAsync();
        }

        _containers.Clear();
    }

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
        Assert.That(reduced.ResidentAssemblyNames, Does.Not.Contain("ModFixture.Patch" + removed));
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
        services.FailingModId = "patch.b";
        var failed = await engine.ApplyAsync([a, b]);

        Assert.That(services.Observations, Does.Contain("itemBA"));
        Assert.That(failed.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(failed));
        Assert.That(failed.ResidentAssemblyNames, Does.Not.Contain("ModFixture.PatchB"));
        Assert.That(Target("item"), Is.EqualTo("itemA"));

        services.FailingModId = null;
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
    private ModEngine CreateEngine(Services services)
    {
        var paths = Directory.GetFiles(TestContext.CurrentContext.TestDirectory, "*.dll");
        var registrations = new ServiceCollection();
        registrations.AddSingleton(typeof(PatchLifecycleTests).GetMethod(services.IsExitTarget ? nameof(ExitTarget) : nameof(Target))
            ?? throw new InvalidOperationException("Missing patch target."));
        registrations.AddSingleton<Action>(() => services.Observations.Add(services.IsExitTarget ? ExitTarget("item") : Target("item")));
        registrations.AddSingleton<Action<string>>(services.Observations.Add);
        registrations.AddTransient(_ => services.FailingModId ?? string.Empty);

        var container = new HostServiceContainer(registrations);
        _containers.Add(container);
        return new ModEngine(new HostAssemblyCatalog(paths), container, new ModEngineOptions
        {
            CollectionAttempts = 30,
            CollectionDelay = TimeSpan.FromMilliseconds(10)
        });
    }

    /// <summary>
    /// Selects one fixture without declaring a dependency on the other patch owner.
    /// </summary>
    private static ModDefinition Definition(string suffix)
    {
        var assembly = $"ModFixture.Patch{suffix}.dll";
        return new ModDefinition(new ModInstallation
        {
            InstallationId = suffix,
            Directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures"),
            Manifest = new ModManifest
            {
                Id = "patch." + suffix.ToLowerInvariant(),
                Version = "1.0.0",
                Entry = new UniversalModEntry
                {
                    Assembly = assembly,
                    Type = "ModFixture.Patching.Entry",
                    Framework = "net10.0",
                    ModApiVersion = "[1.0,2.0)",
                    Assemblies = [assembly]
                }
            }
        }, Path.GetTempPath(), "win-x64", "1.0.0", "1.0.0", "net10.0");
    }

    /// <summary>
    /// Requires confirmed cleanup instead of accepting a restart fallback as successful removal.
    /// </summary>
    private static void AssertApplied(ModApplyResult result)
    {
        Assert.That(result.Status, Is.EqualTo(ModApplyStatus.Applied), Describe(result));
    }

    /// <summary>
    /// Formats diagnostics without retaining collectible exception objects.
    /// </summary>
    private static string Describe(ModApplyResult result)
    {
        return string.Join(Environment.NewLine, result.Diagnostics.Select(item => item.Code + ": " + item.Detail));
    }

    /// <summary>
    /// Supplies framework-owned observations and a host method to both collectible mods.
    /// </summary>
    private sealed class Services
    {
        /// <summary>
        /// Gets whether this test uses the independently retained process-exit target.
        /// </summary>
        internal bool IsExitTarget { get; init; }

        /// <summary>
        /// Gets or sets the mod whose initialization should fail after its hook becomes effective.
        /// </summary>
        internal string? FailingModId { get; set; }

        /// <summary>
        /// Gets results observed immediately after hook installation.
        /// </summary>
        internal List<string> Observations { get; } = [];

    }
}
