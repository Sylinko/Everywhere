using System.IO.Pipes;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Diagnostics;
using Everywhere.ProcessIsolation.Hosts.Input;
using Everywhere.ProcessIsolation.Hosts.Lifecycle;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Tests;

[TestFixture]
public sealed class InputHostRpcTests
{
    [Test]
    public async Task InputContracts_WhenBound_RouteStateAndEveryNotification()
    {
        var pair = await ConnectionPair.CreateAsync();
        await using var server = pair.Server;
        await using var client = pair.Client;
        var input = new TestInputHostRpc();
        var notifications = new TestInputNotifications();
        InputHostRpcBinding.Bind(server, input);
        InputHostNotificationRpcBinding.Bind(client, notifications);
        server.Start();
        client.Start();

        var state = new ApplyInputStateRequest
        {
            KeyboardRegistrations =
            [
                new InputKeyboardRegistration
                {
                    RegistrationId = 7,
                    Key = 42,
                    Modifiers = 3
                }
            ],
            MouseRegistrations =
            [
                new InputMouseRegistration
                {
                    RegistrationId = 8,
                    Button = 2,
                    DelayTicks = TimeSpan.FromMilliseconds(250).Ticks
                }
            ],
            CaptureId = 9
        };
        var response = await new InputHostRpcClient(client).ApplyStateAsync(state);

        var publisher = new InputHostNotificationRpcClient(server);
        await publisher.ShortcutTriggeredAsync(
            new ShortcutTriggeredNotification
            {
                RegistrationId = 7,
                Sequence = 1,
                UtcTicks = DateTimeOffset.UtcNow.UtcTicks
            });
        await publisher.CaptureChangedAsync(
            new ShortcutCaptureChangedNotification
            {
                CaptureId = 9,
                Sequence = 2,
                UtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                Key = 42,
                Modifiers = 3
            });
        await publisher.CaptureFinishedAsync(
            new ShortcutCaptureFinishedNotification
            {
                CaptureId = 9,
                Sequence = 3,
                UtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                Key = 42,
                Modifiers = 3
            });

        await notifications.AllReceived.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(response.IsApplied, Is.True);
            Assert.That(input.State?.KeyboardRegistrations.Single().RegistrationId, Is.EqualTo(7));
            Assert.That(input.State?.MouseRegistrations.Single().RegistrationId, Is.EqualTo(8));
            Assert.That(input.State?.CaptureId, Is.EqualTo(9));
            Assert.That(notifications.TriggeredRegistrationId, Is.EqualTo(7));
            Assert.That(notifications.ChangedCaptureId, Is.EqualTo(9));
            Assert.That(notifications.FinishedCaptureId, Is.EqualTo(9));
        });
    }

    [Test]
    public async Task HostDiagnosticsContract_WhenBound_ForwardsStructuredLogEntry()
    {
        var pair = await ConnectionPair.CreateAsync();
        await using var server = pair.Server;
        await using var client = pair.Client;
        var diagnostics = new TestHostDiagnostics();
        HostDiagnosticsRpcBinding.Bind(client, diagnostics);
        server.Start();
        client.Start();

        await new HostDiagnosticsRpcClient(server).LogAsync(
            new HostLogNotification
            {
                Level = HostLogLevel.Warning,
                Source = "TestHost",
                Message = "Fallback selected.",
                ExceptionText = "Example exception"
            });

        var notification = await diagnostics.Received.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Multiple(() =>
        {
            Assert.That(notification.Level, Is.EqualTo(HostLogLevel.Warning));
            Assert.That(notification.Source, Is.EqualTo("TestHost"));
            Assert.That(notification.Message, Is.EqualTo("Fallback selected."));
            Assert.That(notification.ExceptionText, Is.EqualTo("Example exception"));
        });
    }

    [Test]
    public async Task RoleHostRunner_WithRoleSession_BindsDrainsAndDisposesSession()
    {
        var endpoint = $"Everywhere.Test.InputRole.{Guid.NewGuid():N}";
        var session = new TestRoleSession();
        using var runnerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            () => session,
            runnerCancellation.Token);

        await using var stream = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await stream.ConnectAsync(5000);
        await using var connection = new RpcConnection(stream, isServer: false);
        connection.Start();

        var identity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
        var handshake = await connection.PerformHandshakeAsync(
            new RpcHandshake
            {
                AssemblyInformationalVersion = identity.AssemblyInformationalVersion,
                Role = identity.WireName,
                ProcessId = identity.ProcessId,
                DesktopSessionId = identity.DesktopSessionId
            });
        RpcHandshakeValidator.ValidateAcceptedPeer(handshake, ProcessRole.Input, identity);

        var stateResponse = await new InputHostRpcClient(connection).ApplyStateAsync(
            new ApplyInputStateRequest
            {
                KeyboardRegistrations = [],
                MouseRegistrations = [],
                CaptureId = 0
            });
        var shutdown = await new HostLifecycleRpcClient(connection).ShutdownAsync(new ShutdownRequest());
        var exitCode = await runner.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Multiple(() =>
        {
            Assert.That(stateResponse.IsApplied, Is.True);
            Assert.That(shutdown.Accepted, Is.True);
            Assert.That(exitCode, Is.Zero);
            Assert.That(session.Bound, Is.True);
            Assert.That(session.AuthenticatedProcessId, Is.EqualTo(identity.ProcessId));
            Assert.That(session.DrainCallCount, Is.GreaterThanOrEqualTo(1));
            Assert.That(session.DisposedAfterDraining, Is.True);
        });
    }

    private sealed class TestInputHostRpc : IInputHostRpc
    {
        public ApplyInputStateRequest? State { get; private set; }

        public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
            ApplyInputStateRequest request,
            CancellationToken cancellationToken = default)
        {
            State = request;
            return ValueTask.FromResult(new ApplyInputStateResponse { IsApplied = true });
        }
    }

    private sealed class TestInputNotifications : IInputHostNotificationRpc
    {
        public Task AllReceived => _allReceived.Task;

        public ulong TriggeredRegistrationId { get; private set; }

        public ulong ChangedCaptureId { get; private set; }

        public ulong FinishedCaptureId { get; private set; }

        private readonly TaskCompletionSource _allReceived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _receivedCount;

        public ValueTask ShortcutTriggeredAsync(
            ShortcutTriggeredNotification notification,
            CancellationToken cancellationToken = default)
        {
            TriggeredRegistrationId = notification.RegistrationId;
            CompleteOne();
            return ValueTask.CompletedTask;
        }

        public ValueTask CaptureChangedAsync(
            ShortcutCaptureChangedNotification notification,
            CancellationToken cancellationToken = default)
        {
            ChangedCaptureId = notification.CaptureId;
            CompleteOne();
            return ValueTask.CompletedTask;
        }

        public ValueTask CaptureFinishedAsync(
            ShortcutCaptureFinishedNotification notification,
            CancellationToken cancellationToken = default)
        {
            FinishedCaptureId = notification.CaptureId;
            CompleteOne();
            return ValueTask.CompletedTask;
        }

        private void CompleteOne()
        {
            if (Interlocked.Increment(ref _receivedCount) == 3)
            {
                _allReceived.TrySetResult();
            }
        }
    }

    private sealed class TestHostDiagnostics : IHostDiagnosticsRpc
    {
        public Task<HostLogNotification> Received => _received.Task;

        private readonly TaskCompletionSource<HostLogNotification> _received =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask LogAsync(
            HostLogNotification notification,
            CancellationToken cancellationToken = default)
        {
            _received.TrySetResult(notification);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TestRoleSession : IProcessRoleSession, IInputHostRpc
    {
        public bool Bound { get; private set; }

        public long AuthenticatedProcessId { get; private set; }

        public int DrainCallCount { get; private set; }

        public bool DisposedAfterDraining { get; private set; }

        public void Bind(RpcConnection connection)
        {
            Bound = true;
            InputHostRpcBinding.Bind(connection, this);
        }

        public void OnAuthenticated(RpcHandshake peer) => AuthenticatedProcessId = peer.ProcessId;

        public ValueTask BeginDrainingAsync(CancellationToken cancellationToken = default)
        {
            DrainCallCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposedAfterDraining = DrainCallCount > 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask<ApplyInputStateResponse> ApplyStateAsync(
            ApplyInputStateRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApplyInputStateResponse { IsApplied = true });
    }

    private sealed class ConnectionPair
    {
        public RpcConnection Server { get; }

        public RpcConnection Client { get; }

        private ConnectionPair(RpcConnection server, RpcConnection client)
        {
            Server = server;
            Client = client;
        }

        public static async Task<ConnectionPair> CreateAsync()
        {
            var pipeName = $"Everywhere.Test.Input.{Guid.NewGuid():N}";
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
            return new ConnectionPair(
                new RpcConnection(serverStream, isServer: true, options),
                new RpcConnection(clientStream, isServer: false, options));
        }
    }
}
