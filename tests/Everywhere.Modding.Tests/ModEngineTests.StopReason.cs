using Everywhere.Modding.Abstractions;
using Everywhere.Modding.Hosting;

namespace Everywhere.Modding.Tests;

public sealed partial class ModEngineTests
{
    /// <summary>
    /// Cancellation callbacks and entry disposal see the same reason, assigned after initialization starts.
    /// </summary>
    [TestCase("unload", "normal", ModStopReason.Unload)]
    [TestCase("failure", "initialize-failure", ModStopReason.Unload)]
    [TestCase("exit", "normal", ModStopReason.Exit)]
    [TestCase("pending-exit", "initialize-cancellation", ModStopReason.Exit)]
    public async Task StopReason_RetirementPath_IsVisibleBeforeCancellationAndDisposal(
        string path, string mode, ModStopReason expectedReason)
    {
        var reasons = new List<ModStopReason?>();
        var services = new Services { Mode = mode, ObserveStopReason = reasons.Add };
        await using var engine = CreateEngine(services);
        var result = await engine.ApplyAsync([Definition("B")]);

        switch (path)
        {
            case "failure":
            {
                Assert.That(engine.Mods.Single().State, Is.EqualTo(ModState.Failed));
                Assert.That(result.ResidentAssemblyNames, Is.Empty);
                break;
            }
            case "pending-exit":
            {
                Assert.That(result.Status, Is.EqualTo(ModApplyStatus.RestartRequired), Describe(result));
                await engine.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                break;
            }
            case "unload":
            {
                AssertApplied(result);
                AssertApplied(await engine.ApplyAsync([]));
                break;
            }
            case "exit":
            {
                AssertApplied(result);
                await engine.DisposeAsync();
                break;
            }
        }

        Assert.That(reasons, Is.EqualTo(new ModStopReason?[] { null, expectedReason, expectedReason }));
    }

    /// <summary>
    /// Exit joins a timed-out unload without changing its reason or calling the disposer again.
    /// </summary>
    [Test]
    public async Task StopReason_ExitJoinsTimedOutUnload_PreservesOriginalReason()
    {
        var reasons = new List<ModStopReason?>();
        var services = new Services { Mode = "disposal-timeout", ObserveStopReason = reasons.Add };
        await using var engine = CreateEngine(services);

        try
        {
            AssertApplied(await engine.ApplyAsync([Definition("B")]));
            Assert.That((await engine.ApplyAsync([])).Status, Is.EqualTo(ModApplyStatus.RestartRequired));
            var exit = engine.DisposeAsync().AsTask();
            Assert.That(exit.IsCompleted, Is.False);
            services.Gate.TrySetResult();
            await exit.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.That(reasons, Is.EqualTo(new ModStopReason?[] { null, ModStopReason.Unload, ModStopReason.Unload }));
        }
        finally
        {
            services.Gate.TrySetResult();
        }
    }
}
