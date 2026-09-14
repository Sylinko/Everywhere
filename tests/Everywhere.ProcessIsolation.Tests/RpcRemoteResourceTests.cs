using System.IO.Pipes;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Tests;

[TestFixture]
public sealed class RpcRemoteResourceTests
{
    [Test]
    public async Task RegisterAsync_WhenReleaseArrivedFirst_DisposesWithoutPublishingResource()
    {
        await using var registry = new RpcRemoteResourceRegistry();
        var resource = new AsyncTestResource();

        await registry.ReleaseResourceAsync(new RpcResourceReleaseRequest { ResourceId = 42 });
        var registered = await registry.RegisterAsync(42, resource);

        Assert.Multiple(() =>
        {
            Assert.That(registered, Is.False);
            Assert.That(resource.DisposeCount, Is.EqualTo(1));
            Assert.That(registry.Count, Is.Zero);
        });
    }

    [Test]
    public async Task RpcSafeHandle_WhenDisposed_ReleasesRegisteredPeerResource()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        await using var registry = new RpcRemoteResourceRegistry();
        registry.Bind(server);
        server.Start();
        client.Start();
        var releaseQueue = client.GetSafeHandleReleaseQueue();
        var resource = new AsyncTestResource();
        var resourceId = releaseQueue.AllocateResourceId();
        Assert.That(await registry.RegisterAsync(resourceId, resource), Is.True);
        using var handle = new TestHandle(resourceId, releaseQueue);
        
        handle.Dispose();
        await WaitForAsync(() => resource.DisposeCount == 1);

        Assert.That(registry.Count, Is.Zero);
    }

    [Test]
    public async Task DisposeAsync_WhenRegistryOwnsResources_ReleasesEveryRegisteredResource()
    {
        await using var registry = new RpcRemoteResourceRegistry();
        var first = new AsyncTestResource();
        var second = new AsyncTestResource();
        Assert.That(await registry.RegisterAsync(1, first), Is.True);
        Assert.That(await registry.RegisterAsync(2, second), Is.True);

        await registry.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(first.DisposeCount, Is.EqualTo(1));
            Assert.That(second.DisposeCount, Is.EqualTo(1));
            Assert.That(registry.Count, Is.Zero);
        });
    }

    [Test]
    public async Task Consume_WhenProtocolAlreadyTransferredOwnership_RemovesRegistrationWithoutDisposal()
    {
        await using var registry = new RpcRemoteResourceRegistry();
        var resource = new AsyncTestResource();
        Assert.That(await registry.RegisterAsync(42, resource), Is.True);

        var consumed = registry.Consume(42);
        await registry.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.That(consumed, Is.True);
            Assert.That(resource.DisposeCount, Is.Zero);
            Assert.That(registry.Count, Is.Zero);
        });
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class TestHandle(long resourceId, IRpcSafeHandleReleaser releaser) : RpcSafeHandle(resourceId, releaser);

    private sealed class AsyncTestResource : IAsyncDisposable
    {
        public int DisposeCount => Volatile.Read(ref _disposeCount);

        private int _disposeCount;

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
