using System.Threading.Tasks.Sources;
using Everywhere.Plugin.Abstractions;

namespace Everywhere.Plugin.Tests;

/// <summary>
/// Verifies disposal ordering across ValueTask fast/slow paths and the public registration boundary.
/// </summary>
public sealed class PluginResourceTests
{
    /// <summary>
    /// Synchronous ValueTask completion must consume its source exactly once without invoking IDisposable.
    /// </summary>
    [Test]
    public async Task DisposeAsync_CompletedValueTaskSource_ConsumesOnceAndPrefersAsync()
    {
        var resources = new PluginResource();
        var resource = new CompletionResource();
        resource.Complete();
        resources.Register((IDisposable)resource);
        var pending = resources.DisposeAsync();

        Assert.That(pending.IsCompletedSuccessfully, Is.True);
        await pending;
        await resources.DisposeAsync();
        Assert.That(resource.AsyncCalls, Is.EqualTo(1));
        Assert.That(resource.ResultReads, Is.EqualTo(1));
        Assert.That(resource.SyncCalls, Is.Zero);
    }

    /// <summary>
    /// Waiting and faulted results preserve reverse order, consume each source once and continue after failure.
    /// </summary>
    [Test]
    public async Task DisposeAsync_PendingResourceFails_ContinuesInOrderAndRejectsRegistration()
    {
        var events = new List<string>();
        var resources = new PluginResource();
        var pendingResource = new CompletionResource();
        resources.Register(new CallbackResource(() => events.Add("first")));
        resources.Register(new CallbackResource(() => throw new InvalidOperationException("sync failure")));
        resources.Register(pendingResource);
        resources.Register(new CallbackResource(() => events.Add("last")));
        var disposal = resources.DisposeAsync();

        Assert.That(disposal.IsCompleted, Is.False);
        Assert.That(events, Is.EqualTo(new[] { "last" }));
        var rejected = new CallbackResource(() => events.Add("rejected"));
        Assert.Throws<InvalidOperationException>(() => resources.Register(rejected));
        pendingResource.Fail();
        var error = Assert.ThrowsAsync<AggregateException>(async () => await disposal);

        Assert.That(error?.InnerExceptions, Has.Count.EqualTo(2));
        Assert.That(events, Is.EqualTo(new[] { "last", "first" }));
        Assert.That(pendingResource.AsyncCalls, Is.EqualTo(1));
        Assert.That(pendingResource.ResultReads, Is.EqualTo(1));
        rejected.Dispose();
        await resources.DisposeAsync();
    }

    /// <summary>
    /// Closing does not release live resources, and Default-owned assemblies cannot create implicit containers.
    /// </summary>
    [Test]
    public async Task Register_ClosedOrUnmanagedOwner_DoesNotTakeOwnership()
    {
        var calls = 0;
        var resource = new CallbackResource(() => calls++);
        var resources = new PluginResource();
        Assert.Throws<ArgumentException>(() => resources.Register(new object()));
        Assert.Throws<InvalidOperationException>(() => PluginResource.Register<PluginResourceTests>(resource));
        resources.Register(resource);
        resources.Close();
        Assert.That(calls, Is.Zero);
        Assert.Throws<InvalidOperationException>(() => resources.Register(resource));
        await resources.DisposeAsync();
        Assert.That(calls, Is.EqualTo(1));
    }

    /// <summary>
    /// Exposes observable disposal without a framework subscription or external ownership.
    /// </summary>
    private sealed class CallbackResource(Action callback) : IDisposable
    {
        /// <inheritdoc />
        public void Dispose() => callback();
    }

    /// <summary>
    /// Models a reusable ValueTask source whose result must be consumed once on either execution path.
    /// </summary>
    private sealed class CompletionResource : IDisposable, IAsyncDisposable, IValueTaskSource
    {
        /// <summary>
        /// Gets synchronous disposal calls, which must remain zero under the asynchronous policy.
        /// </summary>
        internal int SyncCalls { get; private set; }

        /// <summary>
        /// Gets asynchronous disposal invocations independently of result consumption.
        /// </summary>
        internal int AsyncCalls { get; private set; }

        /// <summary>
        /// Gets completed source consumptions.
        /// </summary>
        internal int ResultReads { get; private set; }

        /// <summary>
        /// Controls completion and resumes the cleanup continuation outside the producer's call stack.
        /// </summary>
        private ManualResetValueTaskSourceCore<bool> _source = new() { RunContinuationsAsynchronously = true };

        /// <summary>
        /// Completes the result before cleanup begins.
        /// </summary>
        internal void Complete() => _source.SetResult(true);

        /// <summary>
        /// Resumes a waiting cleanup with a failure.
        /// </summary>
        internal void Fail() => _source.SetException(new InvalidOperationException("async failure"));

        /// <inheritdoc />
        public void Dispose() => SyncCalls++;

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            AsyncCalls++;
            return new ValueTask(this, _source.Version);
        }

        /// <inheritdoc />
        public void GetResult(short token)
        {
            ResultReads++;
            _source.GetResult(token);
        }

        /// <inheritdoc />
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);

        /// <inheritdoc />
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            _source.OnCompleted(continuation, state, token, flags);
        }
    }
}