using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Tests;

[TestFixture]
public sealed class RpcDuplexTests
{
    [Test]
    public async Task InvokeAsync_WhenBothPeersUseSameCorrelationId_RoutesBothResponses()
    {
        await using var pair = await RpcPair.CreateAsync();
        var bothHandlersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredHandlerCount = 0;
        pair.Server.RegisterRequestHandler<int, int>(0x20001, HandleServerRequestAsync);
        pair.Client.RegisterRequestHandler<int, int>(0x20001, HandleClientRequestAsync);
        pair.Start();

        var clientInvocation = pair.Client.InvokeAsync<int, int>(0x20001, 1).AsTask();
        var serverInvocation = pair.Server.InvokeAsync<int, int>(0x20001, 2).AsTask();
        await Task.WhenAll(clientInvocation, serverInvocation).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(clientInvocation.Result, Is.EqualTo(1001));
            Assert.That(serverInvocation.Result, Is.EqualTo(2002));
        });
        return;

        async ValueTask<int> HandleServerRequestAsync(int value, CancellationToken cancellationToken)
        {
            SignalHandlerEntered();
            await bothHandlersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return value + 1000;
        }

        async ValueTask<int> HandleClientRequestAsync(int value, CancellationToken cancellationToken)
        {
            SignalHandlerEntered();
            await bothHandlersEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            return value + 2000;
        }

        void SignalHandlerEntered()
        {
            if (Interlocked.Increment(ref enteredHandlerCount) == 2) bothHandlersEntered.TrySetResult();
        }
    }

    [Test]
    public async Task NotificationAndStream_WhenInitiatedByBothPeers_RouteIndependently()
    {
        await using var pair = await RpcPair.CreateAsync();
        var serverNotification = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clientNotification = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Server.RegisterNotificationHandler<int>(0x20002, (value, _) =>
        {
            serverNotification.TrySetResult(value);
            return ValueTask.CompletedTask;
        });
        pair.Client.RegisterNotificationHandler<int>(0x20002, (value, _) =>
        {
            clientNotification.TrySetResult(value);
            return ValueTask.CompletedTask;
        });
        pair.Server.RegisterStreamHandler<int, int>(0x20003, CreateStream);
        pair.Client.RegisterStreamHandler<int, int>(0x20003, CreateStream);
        pair.Start();

        await pair.Client.SendNotificationAsync(0x20002, 10);
        await pair.Server.SendNotificationAsync(0x20002, 20);
        var fromServer = ReadStreamAsync(pair.Client.InvokeStreamAsync<int, int>(0x20003, 100));
        var fromClient = ReadStreamAsync(pair.Server.InvokeStreamAsync<int, int>(0x20003, 200));
        await Task.WhenAll(fromServer, fromClient, serverNotification.Task, clientNotification.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(serverNotification.Task.Result, Is.EqualTo(10));
            Assert.That(clientNotification.Task.Result, Is.EqualTo(20));
            Assert.That(fromServer.Result, Is.EqualTo(new[] { 100, 101, 102 }));
            Assert.That(fromClient.Result, Is.EqualTo(new[] { 200, 201, 202 }));
        });
    }

    [Test]
    public async Task InvokeAsync_WhenServerCancelsReverseRequest_CancelsClientHandler()
    {
        await using var pair = await RpcPair.CreateAsync();
        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        pair.Client.RegisterRequestHandler<int, int>(0x20004, async (_, cancellationToken) =>
        {
            handlerStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        });
        pair.Start();

        using var cancellation = new CancellationTokenSource();
        var invocation = pair.Server.InvokeAsync<int, int>(0x20004, 0, cancellation.Token).AsTask();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        Assert.CatchAsync<OperationCanceledException>(async () => await invocation);
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task Handlers_WhenRegisteredAfterStart_AcceptReverseRequestsAndStreams()
    {
        await using var pair = await RpcPair.CreateAsync();
        pair.Start();
        pair.Client.RegisterRequestHandler<int, int>(0x20005, static (value, _) => ValueTask.FromResult(value + 1));
        pair.Client.RegisterStreamHandler<int, int>(0x20006, CreateStream);

        var response = await pair.Server.InvokeAsync<int, int>(0x20005, 41);
        var stream = await ReadStreamAsync(pair.Server.InvokeStreamAsync<int, int>(0x20006, 7));

        Assert.Multiple(() =>
        {
            Assert.That(response, Is.EqualTo(42));
            Assert.That(stream, Is.EqualTo(new[] { 7, 8, 9 }));
        });
    }

    [Test]
    public async Task InvokeStreamAsync_WhenChunkEnqueueFails_PreservesSequenceAndConnection()
    {
        var options = new RpcConnectionOptions
        {
            RequireHandshake = false,
            MaximumStreamChunkPayloadBytes = 1024,
        };
        await using var pair = await RpcPair.CreateAsync(options);
        pair.Server.RegisterStreamHandler<int, byte[]>(0x20007, CreateOversizedStream);
        pair.Server.RegisterRequestHandler<int, int>(0x20008, static (value, _) => ValueTask.FromResult(value + 1));
        pair.Start();

        var received = new List<byte[]>();
        var exception = Assert.ThrowsAsync<RpcRemoteException>(async () =>
        {
            await foreach (var item in pair.Client.InvokeStreamAsync<int, byte[]>(0x20007, 0)) received.Add(item);
        });
        var response = await pair.Client.InvokeAsync<int, int>(0x20008, 41);

        Assert.Multiple(() =>
        {
            Assert.That(received, Has.Count.EqualTo(1));
            Assert.That(received[0], Has.Length.EqualTo(16));
            Assert.That(exception?.Code, Is.EqualTo("stream_error"));
            Assert.That(exception?.RemoteMessage, Does.Contain("payload length exceeds"));
            Assert.That(response, Is.EqualTo(42));
        });
    }

    [Test]
    public async Task InvokeStreamAsync_WhenConsumerPauses_ResumesWithoutDataLoss()
    {
        await using var pair = await RpcPair.CreateAsync();
        pair.Server.RegisterStreamHandler<int, byte[]>(0x20009, CreateLargeStream);
        pair.Start();

        var receivedCount = 0;
        await using var enumerator = pair.Client.InvokeStreamAsync<int, byte[]>(0x20009, 80).GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        receivedCount++;
        // This verifies that the bounded receive path tolerates a suspended consumer. It does not
        // attempt to measure when backpressure reaches the remote stream producer.
        await Task.Delay(250);
        while (await enumerator.MoveNextAsync()) receivedCount++;

        Assert.That(receivedCount, Is.EqualTo(80));
    }

    private static async IAsyncEnumerable<int> CreateStream(int value, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < 3; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value + index;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<byte[]> CreateOversizedStream(
        int _,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        yield return new byte[16];
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        yield return new byte[2048];
    }

    private static async IAsyncEnumerable<byte[]> CreateLargeStream(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return new byte[128 * 1024];
            await Task.Yield();
        }
    }

    private static async Task<int[]> ReadStreamAsync(IAsyncEnumerable<int> stream)
    {
        var result = new List<int>();
        await foreach (var value in stream) result.Add(value);
        return [.. result];
    }

    private sealed class RpcPair : IAsyncDisposable
    {
        public RpcConnection Server { get; }
        public RpcConnection Client { get; }

        private RpcPair(NamedPipeServerStream serverStream, NamedPipeClientStream clientStream, RpcConnectionOptions options)
        {
            Server = new RpcConnection(serverStream, isServer: true, options);
            Client = new RpcConnection(clientStream, isServer: false, options);
        }

        public static async Task<RpcPair> CreateAsync(RpcConnectionOptions? options = null)
        {
            var pipeName = TestPipeNames.Create();
            var serverStream = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var waitForConnection = serverStream.WaitForConnectionAsync();
            var clientStream = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await clientStream.ConnectAsync(5000);
            await waitForConnection;
            return new RpcPair(serverStream, clientStream, options ?? new RpcConnectionOptions { RequireHandshake = false });
        }

        public void Start()
        {
            Server.Start();
            Client.Start();
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
