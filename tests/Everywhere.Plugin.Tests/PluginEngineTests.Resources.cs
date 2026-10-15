using Everywhere.Plugin.Hosting;
using Everywhere.Plugin.Hosting.Installation;
using Everywhere.Plugin.Packaging;

namespace Everywhere.Plugin.Tests;

public sealed partial class PluginEngineTests
{
    /// <summary>
    /// A shared DLL retains its static subscriptions until its final user stops, then supports a fresh load.
    /// </summary>
    [Test]
    public async Task ApplyAsync_SharedStaticResources_DisposesOnlyAfterLastActivation()
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var a = ResourceDefinition("a");
        var b = ResourceDefinition("b");

        for (var cycle = 0; cycle < 2; cycle++)
        {
            AssertApplied(await engine.ApplyAsync([a, b]));
            services.Events.Clear();
            AssertApplied(await engine.ApplyAsync([b]));
            Assert.That(services.Events, Is.EqualTo(new[] { "activation:resource.a" }));

            services.Events.Clear();
            var stopped = await engine.ApplyAsync([]);
            AssertApplied(stopped);
            Assert.That(stopped.ResidentAssemblyNames, Is.Empty);
            Assert.That(services.Events, Is.EqualTo(new[]
            {
                "activation:resource.b", "assembly:async-start", "assembly:async-end", "assembly:type", "assembly:generic", "assembly:module"
            }));
        }
    }

    /// <summary>
    /// Resources registered before entry construction and before an initialization failure still collect.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task ApplyAsync_EntryFails_ReleasesStaticResourcesBeforeRetry(bool hasStaticConstructorFailure)
    {
        var services = new Services { Mode = hasStaticConstructorFailure ? "normal" : "initialize-failure" };
        var engine = CreateEngine(services);
        var definition = ResourceDefinition("a", hasStaticConstructorFailure ? "FailedEntry" : "Entry");
        var failed = await engine.ApplyAsync([definition]);

        Assert.That(failed.Status, Is.EqualTo(PluginApplyStatus.ActivationFailed), Describe(failed));
        Assert.That(failed.ResidentAssemblyNames, Is.Empty);
        services.Mode = "normal";
        AssertApplied(await engine.ApplyAsync([ResourceDefinition("a")]));
        AssertApplied(await engine.ApplyAsync([]));
    }

    /// <summary>
    /// A failed static disposer does not skip earlier registrations, but blocks physical unloading.
    /// </summary>
    [Test]
    public async Task ApplyAsync_StaticCleanupFails_CompletesRemainingResourcesAndRequiresRestart()
    {
        var services = new Services { Mode = "cleanup-failure" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([ResourceDefinition("a")]));
        var result = await engine.ApplyAsync([]);

        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired), Describe(result));
        Assert.That(result.Diagnostics.Select(item => item.Code), Does.Contain("AssemblyCleanupFailed"));
        Assert.That(services.Events, Does.Contain("assembly:generic"));
        Assert.That(result.ResidentAssemblyNames, Does.Contain("PluginFixture.Resources"));
    }

    /// <summary>
    /// An incomplete disposer retains its owner and prevents another change, including after it eventually finishes.
    /// </summary>
    [Test]
    public async Task ApplyAsync_StaticCleanupTimesOut_RetainsOwnerWithoutConcurrentDisposal()
    {
        var services = new Services { Mode = "cleanup-timeout" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([ResourceDefinition("a")]));
        var result = await engine.ApplyAsync([]);
        Assert.That(result.Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
        Assert.That(result.Diagnostics.Select(item => item.Code), Does.Contain("AssemblyCleanupTimedOut"));
        Assert.That(services.Events, Does.Not.Contain("assembly:type"));
        Assert.That(result.ResidentAssemblyNames, Does.Contain("PluginFixture.Resources"));
        services.Gate.SetResult();

        Assert.That(() =>
        {
            lock (services.Events) return services.Events.Contains("assembly:generic");
        }, Is.True.After(2000, 10));
        Assert.That((await engine.ApplyAsync([])).Status, Is.EqualTo(PluginApplyStatus.RestartRequired));
    }

    /// <summary>
    /// Selects distinct activations backed by the same ordinary DLL and resource owner.
    /// </summary>
    private PluginDefinition ResourceDefinition(string id, string entry = "Entry")
    {
        return new PluginDefinition(new PluginInstallation
        {
            InstallationId = "resource-" + id,
            Directory = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures"),
            Manifest = new PluginManifest
            {
                Id = "resource." + id,
                Version = "1.0.0",
                Entry = new UniversalPluginEntry
                {
                    Assembly = "PluginFixture.Resources.dll",
                    Type = "PluginFixture.Resources." + entry,
                    Framework = "net10.0",
                    PluginApiVersion = "[1.0,2.0)",
                    Assemblies = ["PluginFixture.Resources.dll"]
                }
            }
        }, Path.Combine(_dataDirectory, id), "win-x64", "1.0.0", "1.0.0", "net10.0");
    }
}