using System.Runtime.Loader;
using Everywhere.Modding.Hosting;

namespace Everywhere.Modding.Tests;

public sealed partial class ModEngineTests
{
    /// <summary>
    /// Exit disposes consumers first and preserves installed registrations and assembly owners.
    /// </summary>
    [Test]
    public async Task DisposeAsync_ActiveMods_DisposesOnceWithoutReversalOrUnloading()
    {
        var services = new Services { Mode = "retained" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("A"), Definition("B")]));
        var retained = services.Retained ?? throw new AssertionException("Missing retained fixture.");
        var owner = AssemblyLoadContext.GetLoadContext(retained.GetType().Assembly)
            ?? throw new AssertionException("Missing fixture owner.");
        var hasUnloaded = false;
        owner.Unloading += _ => hasUnloaded = true;
        services.Events.Clear();

        var first = engine.DisposeAsync().AsTask();
        var second = engine.DisposeAsync().AsTask();
        Assert.That(second, Is.SameAs(first));
        await first;
        await engine.DisposeAsync();

        Assert.That(services.Events, Is.EqualTo(new[] { "entry-dispose:fixture.a", "entry-dispose:fixture.b" }));
        Assert.That(hasUnloaded, Is.False);
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.ApplyAsync([]));
        services.Retained = null;
    }

    /// <summary>
    /// Exit never retries a disposer. An earlier reversal failure does not become an entry-disposal failure.
    /// </summary>
    [TestCase("disposal-failure", false)]
    [TestCase("cleanup-failure", true)]
    public async Task DisposeAsync_RetirementFailed_PreservesDisposalResultAndContinuesIndependentMods(
        string failure, bool canDisposeDependency)
    {
        var services = new Services { Modes = id => id == "fixture.a" ? failure : "normal" };
        var engine = CreateEngine(services);
        var b = Definition("B");
        var independent = Definition("B", "fixture.independent");
        AssertApplied(await engine.ApplyAsync([Definition("A"), b, independent]));
        Assert.That((await engine.ApplyAsync([b, independent])).Status, Is.EqualTo(ModApplyStatus.RestartRequired));
        services.Events.Clear();

        if (canDisposeDependency)
        {
            await engine.DisposeAsync();
        }
        else
        {
            Assert.ThrowsAsync<InvalidOperationException>(async () => await engine.DisposeAsync());
        }

        Assert.That(services.Events, Does.Contain("entry-dispose:fixture.independent"));
        Assert.That(services.Events, Does.Not.Contain("entry-dispose:fixture.a"));
        Assert.That(services.Events.Contains("entry-dispose:fixture.b"), Is.EqualTo(canDisposeDependency));
        Assert.That(services.Events.Any(value => value.StartsWith("dispose:", StringComparison.Ordinal)), Is.False);
    }

    /// <summary>
    /// A timed-out hot-unload disposer is joined at exit, not invoked a second time or overlapped with reversal.
    /// </summary>
    [Test]
    public async Task DisposeAsync_EntryDisposalStillRunning_JoinsOriginalOperation()
    {
        var services = new Services { Mode = "disposal-timeout" };
        var engine = CreateEngine(services);
        AssertApplied(await engine.ApplyAsync([Definition("B")]));
        Assert.That((await engine.ApplyAsync([])).Status, Is.EqualTo(ModApplyStatus.RestartRequired));

        var closing = engine.DisposeAsync().AsTask();
        Assert.That(closing.IsCompleted, Is.False);
        services.Gate.SetResult();
        await closing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:fixture.b", "entry-dispose:fixture.b" }));
    }

    /// <summary>
    /// Exit waits for initialization whether its host wait is still running or already timed out.
    /// </summary>
    [TestCase(false)]
    [TestCase(true)]
    public async Task DisposeAsync_InitializationPending_WaitsBeforeDisposal(bool hasTimedOut)
    {
        var services = new Services { Mode = "initialize-timeout" };
        var engine = CreateEngine(services);
        var applying = engine.ApplyAsync([Definition("B")]);
        await services.Initialized.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (hasTimedOut) Assert.That((await applying).Status, Is.EqualTo(ModApplyStatus.RestartRequired));

        var closing = engine.DisposeAsync().AsTask();
        Assert.That(closing.IsCompleted, Is.False);
        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:fixture.b" }));
        Assert.ThrowsAsync<ObjectDisposedException>(async () => await engine.ApplyAsync([]));
        services.Gate.SetResult();
        await applying;
        await closing.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:fixture.b", "entry-dispose:fixture.b" }));
    }

    /// <summary>
    /// Cancellation must reach a timed-out initializer before exit waits for it to finish.
    /// </summary>
    [Test]
    public async Task DisposeAsync_InitializerWaitsForCancellation_SignalsBeforeJoining()
    {
        var services = new Services { Mode = "initialize-cancellation" };
        var engine = CreateEngine(services);
        Assert.That((await engine.ApplyAsync([Definition("B")])).Status, Is.EqualTo(ModApplyStatus.RestartRequired));

        await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        Assert.That(services.Events, Is.EqualTo(new[] { "initialize:fixture.b", "entry-dispose:fixture.b" }));
    }

    /// <summary>
    /// Optional synchronous and absent disposal interfaces work on both retirement paths.
    /// </summary>
    [TestCase("SynchronousEntry", false)]
    [TestCase("SynchronousEntry", true)]
    [TestCase("PassiveEntry", false)]
    [TestCase("PassiveEntry", true)]
    public async Task DisposeAsync_OptionalEntryInterfaces_UsesOnlyImplementedContract(string type, bool isExit)
    {
        var services = new Services();
        var engine = CreateEngine(services);
        var original = Definition("B");
        var manifest = original.Installation.Manifest with { Entry = original.Entry with { Type = "ModFixture." + type } };
        var definition = new ModDefinition(original.Installation with { Manifest = manifest },
            original.DataDirectory, "win-x64", "1.0.0", "1.0.0", "net10.0");
        AssertApplied(await engine.ApplyAsync([definition]));

        if (isExit)
        {
            await engine.DisposeAsync();
        }
        else
        {
            AssertApplied(await engine.ApplyAsync([]));
            await engine.DisposeAsync();
        }

        Assert.That(services.Events.Count(value => value == "entry-sync"), Is.EqualTo(type == "SynchronousEntry" ? 1 : 0));
        Assert.That(services.Events.Contains("undo"), Is.EqualTo(!isExit));
    }

    /// <summary>
    /// Owned IO drains on all retirement paths, while a reversal that shares its release does not release twice.
    /// </summary>
    [TestCase("hot")]
    [TestCase("failure")]
    [TestCase("exit")]
    public async Task DisposeAsync_OwnedIoAndSharedReversal_ReleasesOnceAfterWorkFinishes(string path)
    {
        var services = new Services { Mode = path == "failure" ? "owned-io-failure" : "owned-io" };
        var engine = CreateEngine(services);
        var definition = Definition("B");
        var result = await engine.ApplyAsync([definition]);

        if (path == "failure")
        {
            Assert.That(result.Status, Is.EqualTo(ModApplyStatus.CompletedWithErrors), Describe(result));
            Assert.That(result.ResidentAssemblyNames, Is.Empty);
        }
        else
        {
            AssertApplied(result);
            if (path == "hot") AssertApplied(await engine.ApplyAsync([]));
        }

        await engine.DisposeAsync();
        var file = Path.Combine(definition.DataDirectory, "owned.txt");
        Assert.That(File.ReadAllText(file), Is.EqualTo("finished"));
        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.That(services.Events.Count(value => value == "owned-release"), Is.EqualTo(1));
        Assert.That(services.Events.Contains("owned-undo"), Is.EqualTo(path != "exit"));
        if (path != "exit")
        {
            Assert.That(services.Events.IndexOf("owned-release"), Is.LessThan(services.Events.IndexOf("owned-undo")));
        }
    }
}