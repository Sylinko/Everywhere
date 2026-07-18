using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Everywhere.Configuration;
using Everywhere.Skills;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Everywhere.Core.Tests.Skills;

public class SkillManagerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    public async Task RefreshAsync_SerializesConcurrentRequests()
    {
        var provider = new ControlledVirtualSkillProvider();
        var firstDiscovery = provider.BlockNextRefresh();
        var followUpDiscovery = provider.BlockNextRefresh();
        using var source = CreateSource();
        var manager = CreateManager(source, provider);
        var refreshNotifications = 0;
        manager.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SkillManager.IsRefreshing)) refreshNotifications++;
        };

        var firstRefresh = manager.RefreshAsync();
        await firstDiscovery.Started.Task.WaitAsync(Timeout);

        var secondRefresh = manager.RefreshAsync();
        Assert.That(secondRefresh, Is.Not.SameAs(firstRefresh));

        firstDiscovery.Continue.TrySetResult(true);
        await followUpDiscovery.Started.Task.WaitAsync(Timeout);
        Assert.That(manager.IsRefreshing, Is.True);

        followUpDiscovery.Continue.TrySetResult(true);
        await Task.WhenAll(firstRefresh, secondRefresh).WaitAsync(Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(provider.InvocationCount, Is.EqualTo(2));
            Assert.That(refreshNotifications, Is.EqualTo(2));
            Assert.That(manager.IsRefreshing, Is.False);
        });
    }

    [Test]
    public async Task RefreshAsync_CancellingQueuedRequestDoesNotCancelActiveRefresh()
    {
        var provider = new ControlledVirtualSkillProvider();
        var firstDiscovery = provider.BlockNextRefresh();
        using var source = CreateSource();
        var manager = CreateManager(source, provider);

        var sharedRefresh = manager.RefreshAsync();
        await firstDiscovery.Started.Task.WaitAsync(Timeout);

        using var cancellation = new CancellationTokenSource();
        var cancelledWait = manager.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await cancelledWait);

        firstDiscovery.Continue.TrySetResult(true);
        await sharedRefresh.WaitAsync(Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(provider.InvocationCount, Is.EqualTo(1));
            Assert.That(manager.IsRefreshing, Is.False);
        });
    }

    [Test]
    public async Task RefreshAsync_CanRetryAfterDiscoveryFailure()
    {
        var provider = new ControlledVirtualSkillProvider();
        provider.FailNextRefresh();
        using var source = CreateSource();
        var manager = CreateManager(source, provider);

        var failedRefresh = manager.RefreshAsync();
        Assert.ThrowsAsync<InvalidOperationException>(async () => await failedRefresh);
        Assert.That(manager.IsRefreshing, Is.False);

        await manager.RefreshAsync().WaitAsync(Timeout);

        Assert.Multiple(() =>
        {
            Assert.That(provider.InvocationCount, Is.EqualTo(2));
            Assert.That(manager.IsRefreshing, Is.False);
        });
    }

    private static SkillSource CreateSource() =>
        new(Substitute.For<ILogger<SkillSource>>(), () => []);

    private static SkillManager CreateManager(SkillSource source, IVirtualSkillProvider provider) =>
        new(
            new PersistentState(new InMemoryKeyValueStorage()),
            source,
            [provider],
            Substitute.For<ILogger<SkillManager>>());

    private sealed class ControlledVirtualSkillProvider : IVirtualSkillProvider
    {
        private readonly ConcurrentQueue<RefreshControl> _refreshControls = new();
        private int _invocationCount;
        private int _failNextRefresh;

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public RefreshControl BlockNextRefresh()
        {
            var control = new RefreshControl();
            _refreshControls.Enqueue(control);
            return control;
        }

        public void FailNextRefresh() => Interlocked.Exchange(ref _failNextRefresh, 1);

        public async IAsyncEnumerable<VirtualSkill> ListAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _invocationCount);
            if (Interlocked.Exchange(ref _failNextRefresh, 0) != 0)
            {
                throw new InvalidOperationException("Simulated skill discovery failure.");
            }

            if (_refreshControls.TryDequeue(out var control))
            {
                control.Started.TrySetResult(true);
                await control.Continue.Task.WaitAsync(cancellationToken);
            }

            yield break;
        }
    }

    private sealed class RefreshControl
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
