using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Avalonia.Input;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Input;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Everywhere.Core.Tests.ProcessIsolation.Input;

[TestFixture]
public sealed class InputHostShortcutListenerTests
{
    [Test]
    public async Task RegistrationsAndNotifications_WithDuplicateHandlers_SnapshotOnceAndFanOutLocally()
    {
        var source = new TestHostConnectionSource();
        await using var listener = new InputHostShortcutListener(
            source,
            NullLogger<InputHostShortcutListener>.Instance);
        var pair = await InputConnectionPair.CreateAsync();
        await using var server = pair.Server;
        await using var client = pair.Client;
        var host = new TestInputHostRpc();
        InputHostRpcBinding.Bind(server, host);
        server.Start();
        client.Start();

        var callbackCount = 0;
        var fourCallbacks = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action callback = () =>
        {
            if (Interlocked.Increment(ref callbackCount) == 4)
            {
                fourCallbacks.TrySetResult();
            }
        };
        var shortcut = new KeyboardShortcut(Key.K, KeyModifiers.Control | KeyModifiers.Shift);
        using var firstRegistration = listener.Register(shortcut, callback);
        using var secondRegistration = listener.Register(shortcut, callback);
        source.Publish(client);

        var state = await host.NextStateAsync();
        var registration = state.KeyboardRegistrations.Single();
        Assert.That(state.KeyboardRegistrations, Has.Length.EqualTo(1));

        var notifications = new InputHostNotificationRpcClient(server);
        await notifications.ShortcutTriggeredAsync(CreateTrigger(registration.RegistrationId, 1));
        await notifications.ShortcutTriggeredAsync(
            CreateTrigger(registration.RegistrationId, 2, DateTimeOffset.UtcNow.AddSeconds(-10).UtcTicks));
        await notifications.ShortcutTriggeredAsync(CreateTrigger(registration.RegistrationId, 2));
        await notifications.ShortcutTriggeredAsync(CreateTrigger(registration.RegistrationId, 2));
        await notifications.ShortcutTriggeredAsync(CreateTrigger(registration.RegistrationId + 1, 3));
        await notifications.ShortcutTriggeredAsync(CreateTrigger(registration.RegistrationId, 4));

        await fourCallbacks.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.That(callbackCount, Is.EqualTo(4));

        firstRegistration.Dispose();
        secondRegistration.Dispose();
        var emptyState = await host.NextStateAsync();
        Assert.That(emptyState.KeyboardRegistrations, Is.Empty);
    }

    [Test]
    public async Task CaptureNotifications_ForActiveCapture_UpdateAndFinishScope()
    {
        var source = new TestHostConnectionSource();
        await using var listener = new InputHostShortcutListener(
            source,
            NullLogger<InputHostShortcutListener>.Instance);
        var pair = await InputConnectionPair.CreateAsync();
        await using var server = pair.Server;
        await using var client = pair.Client;
        var host = new TestInputHostRpc();
        InputHostRpcBinding.Bind(server, host);
        server.Start();
        client.Start();

        using var scope = listener.StartCaptureKeyboardShortcut();
        var changed = new TaskCompletionSource<KeyboardShortcut>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<KeyboardShortcut>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.PressingShortcutChanged += (_, shortcut) => changed.TrySetResult(shortcut);
        scope.ShortcutFinished += (_, shortcut) => finished.TrySetResult(shortcut);
        source.Publish(client);

        var state = await host.NextStateAsync();
        Assert.That(state.CaptureId, Is.Not.Zero);
        var notifications = new InputHostNotificationRpcClient(server);
        await notifications.CaptureChangedAsync(
            new ShortcutCaptureChangedNotification
            {
                CaptureId = state.CaptureId,
                Sequence = 1,
                UtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                Key = (int)Key.P,
                Modifiers = (int)KeyModifiers.Meta
            });
        await notifications.CaptureFinishedAsync(
            new ShortcutCaptureFinishedNotification
            {
                CaptureId = state.CaptureId,
                Sequence = 2,
                UtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                Key = (int)Key.P,
                Modifiers = (int)KeyModifiers.Meta
            });

        var expected = new KeyboardShortcut(Key.P, KeyModifiers.Meta);
        var changedShortcut = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var finishedShortcut = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(changedShortcut, Is.EqualTo(expected));
            Assert.That(finishedShortcut, Is.EqualTo(expected));
            Assert.That(scope.PressingShortcut, Is.EqualTo(expected));
        });
        var completedState = await host.NextStateAsync();
        Assert.That(completedState.CaptureId, Is.Zero);
    }

    [Test]
    public async Task ReplacementConnection_AfterDisconnect_ReceivesSameRegistrationSnapshot()
    {
        var source = new TestHostConnectionSource();
        await using var listener = new InputHostShortcutListener(
            source,
            NullLogger<InputHostShortcutListener>.Instance);
        using var registration = listener.Register(
            new KeyboardShortcut(Key.E, KeyModifiers.Control),
            static () => { });

        var firstPair = await InputConnectionPair.CreateAsync();
        await using var firstServer = firstPair.Server;
        var firstHost = new TestInputHostRpc();
        InputHostRpcBinding.Bind(firstServer, firstHost);
        firstServer.Start();
        firstPair.Client.Start();
        source.Publish(firstPair.Client);
        var firstState = await firstHost.NextStateAsync();

        await firstPair.Client.DisposeAsync();

        var secondPair = await InputConnectionPair.CreateAsync();
        await using var secondServer = secondPair.Server;
        await using var secondClient = secondPair.Client;
        var secondHost = new TestInputHostRpc();
        InputHostRpcBinding.Bind(secondServer, secondHost);
        secondServer.Start();
        secondClient.Start();
        source.Publish(secondClient);
        var secondState = await secondHost.NextStateAsync();

        Assert.That(
            secondState.KeyboardRegistrations.Single().RegistrationId,
            Is.EqualTo(firstState.KeyboardRegistrations.Single().RegistrationId));
    }

    [Test]
    public async Task ActiveCapture_WhenConnectionDisconnects_IsCancelledLocally()
    {
        var source = new TestHostConnectionSource();
        await using var listener = new InputHostShortcutListener(
            source,
            NullLogger<InputHostShortcutListener>.Instance);
        var pair = await InputConnectionPair.CreateAsync();
        await using var server = pair.Server;
        var host = new TestInputHostRpc();
        InputHostRpcBinding.Bind(server, host);
        server.Start();
        pair.Client.Start();

        using var scope = listener.StartCaptureKeyboardShortcut();
        var finished = new TaskCompletionSource<KeyboardShortcut>(TaskCreationOptions.RunContinuationsAsynchronously);
        scope.ShortcutFinished += (_, shortcut) => finished.TrySetResult(shortcut);
        source.Publish(pair.Client);
        var state = await host.NextStateAsync();
        Assert.That(state.CaptureId, Is.Not.Zero);

        await pair.Client.DisposeAsync();

        var finalShortcut = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.That(finalShortcut, Is.EqualTo(default(KeyboardShortcut)));
    }

    private static ShortcutTriggeredNotification CreateTrigger(ulong registrationId, ulong sequence, long? utcTicks = null) => new()
    {
        RegistrationId = registrationId,
        Sequence = sequence,
        UtcTicks = utcTicks ?? DateTimeOffset.UtcNow.UtcTicks
    };

    private sealed class TestInputHostRpc : IInputHostRpc
    {
        private readonly Channel<ApplyInputStateRequest> _states = Channel.CreateUnbounded<ApplyInputStateRequest>();

        public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
            ApplyInputStateRequest request,
            CancellationToken cancellationToken = default)
        {
            _states.Writer.TryWrite(request);
            return ValueTask.FromResult(new ApplyInputStateResponse { IsApplied = true });
        }

        public Task<ApplyInputStateRequest> NextStateAsync() =>
            _states.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class TestHostConnectionSource : IHostConnectionSource
    {
        private readonly Channel<RpcConnection> _connections = Channel.CreateUnbounded<RpcConnection>();

        public void Publish(RpcConnection connection) => _connections.Writer.TryWrite(connection);

        public async IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(
            ProcessRole role,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var connection in _connections.Reader.ReadAllAsync(cancellationToken))
            {
                yield return connection;
            }
        }
    }

    private sealed class InputConnectionPair
    {
        public RpcConnection Server { get; }

        public RpcConnection Client { get; }

        private InputConnectionPair(RpcConnection server, RpcConnection client)
        {
            Server = server;
            Client = client;
        }

        public static async Task<InputConnectionPair> CreateAsync()
        {
            var pipeName = $"evtest-{Guid.NewGuid():N}"[..23];
            var serverStream = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            var waitForConnection = serverStream.WaitForConnectionAsync();
            var clientStream = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await clientStream.ConnectAsync(5000);
            await waitForConnection;

            var options = new RpcConnectionOptions { RequireHandshake = false };
            return new InputConnectionPair(
                new RpcConnection(serverStream, isServer: true, options),
                new RpcConnection(clientStream, isServer: false, options));
        }
    }
}
