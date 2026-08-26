using System.IO.Pipes;
using System.Runtime.CompilerServices;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Hosts.Control;
using Everywhere.ProcessIsolation.Hosts.Lifecycle;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Watchdog;

namespace Everywhere.ProcessIsolation.Tests;

[TestFixture]
public class ProcessRoleAndRpcTests
{
    [TestCase("--process-role=input", ProcessRole.Input)]
    [TestCase("--process-role", ProcessRole.Automation)]
    public void ParseRole_RecognizesRoleSwitch(string switchValue, ProcessRole expectedRole)
    {
        var args = switchValue == "--process-role"
            ? new[] { switchValue, "automation" }
            : new[] { switchValue };

        Assert.That(ProcessRoleCommandLine.Parse(args), Is.EqualTo(expectedRole));
    }

    [TestCase("--process-role")]
    [TestCase("--process-role=invalid")]
    public void ParseRole_InvalidSwitch_Throws(string switchValue)
    {
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.Parse(new[] { switchValue }));
    }

    [Test]
    public void ParseRole_DuplicateSwitch_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.Parse(
            new[] { "--process-role=input", "--process-role=automation" }));
    }

    [TestCase("--hosts-control=start", HostsControlOperation.Start)]
    [TestCase("--hosts-control", HostsControlOperation.Stop)]
    [TestCase("--hosts-control=install", HostsControlOperation.Install)]
    [TestCase("--hosts-control", HostsControlOperation.Uninstall)]
    public void ParseHostsControl_RecognizesOperation(string switchValue, HostsControlOperation expectedOperation)
    {
        var args = switchValue == "--hosts-control"
            ? new[] { switchValue, expectedOperation.ToString().ToLowerInvariant() }
            : new[] { switchValue };

        Assert.That(ProcessRoleCommandLine.ParseHostsControl(args), Is.EqualTo(expectedOperation));
    }

    [Test]
    public void ParseHostsControl_Absent_ReturnsNull()
    {
        Assert.That(ProcessRoleCommandLine.ParseHostsControl(Array.Empty<string>()), Is.Null);
    }

    [TestCase("--hosts-control")]
    [TestCase("--hosts-control=invalid")]
    public void ParseHostsControl_InvalidOperation_Throws(string switchValue)
    {
        var args = switchValue == "--hosts-control"
            ? new[] { switchValue }
            : new[] { switchValue };

        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.ParseHostsControl(args));
    }

    [Test]
    public void ParseHostsControl_RejectsAdditionalArguments()
    {
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.ParseHostsControl(
            new[] { "--hosts-control=start", "--load-user-profile" }));
    }

    [Test]
    public void ParseHostsControl_RejectsRoleCombination()
    {
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.ParseHostsControl(
            new[] { "--hosts-control=start", "--process-role=input" }));
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.Parse(
            new[] { "--hosts-control=start", "--process-role=input" }));
    }

    [Test]
    public void ParseHostEndpointOverride_UnknownArgument_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.ParseHostEndpointOverride(
            ProcessRole.Input,
            new[] { "--process-role=input", "--unknown" }));
    }

    [Test]
    public void ParseHostEndpointOverride_MismatchedRole_Throws()
    {
        Assert.Throws<ArgumentException>(() => ProcessRoleCommandLine.ParseHostEndpointOverride(
            ProcessRole.Automation,
            new[] { "--process-role=input" }));
    }

    [Test]
    public void ParseHostEndpointOverride_ValidDiagnosticOverride_ReturnsEndpoint()
    {
        var endpoint = ProcessRoleCommandLine.ParseHostEndpointOverride(
            ProcessRole.Input,
            new[] { "--process-role=input", "--rpc-endpoint", "Everywhere.Test.Endpoint" });

        Assert.That(endpoint, Is.EqualTo("Everywhere.Test.Endpoint"));
    }

    [Test]
    public void GetDefaultEndpoint_WithSameIdentity_IsStableAndRoleSpecific()
    {
        var endpoint = ProcessRoleNames.GetDefaultEndpoint(ProcessRole.Automation, "desktop-42");
        var sameEndpoint = ProcessRoleNames.GetDefaultEndpoint(ProcessRole.Automation, "desktop-42");
        var otherSessionEndpoint = ProcessRoleNames.GetDefaultEndpoint(ProcessRole.Automation, "desktop-43");

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Is.EqualTo(sameEndpoint));
            Assert.That(endpoint, Is.Not.EqualTo(otherSessionEndpoint));
            Assert.That(endpoint, Does.EndWith(".automation"));
        });
    }

    [Test]
    public void GetMainControlEndpoint_UsesControlIdentityInsteadOfProcessRole()
    {
        var endpoint = ProcessRoleNames.GetMainControlEndpoint("desktop-42");
        var roleEndpoint = ProcessRoleNames.GetDefaultEndpoint(ProcessRole.Main, "desktop-42");

        Assert.Multiple(() =>
        {
            Assert.That(endpoint, Is.Not.EqualTo(roleEndpoint));
            Assert.That(endpoint, Does.EndWith(".main-control"));
        });
    }

    [Test]
    public void FrameHeader_RoundTripsLittleEndianFields()
    {
        Assert.That(RpcProtocolConstants.HeaderSize, Is.EqualTo(28));

        var expected = new RpcFrameHeader(
            RpcFrameKind.Response,
            RpcFrameFlags.None,
            0x0102_0304,
            0x0102_0304_0506_0708,
            42,
            1234);
        var buffer = new byte[RpcProtocolConstants.HeaderSize];

        expected.Write(buffer);
        var actual = RpcFrameHeader.Read(buffer, 1024 * 1024);

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public async Task Connection_RemainsAliveWhileCompletelyIdle()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions
        {
            RequireHandshake = false,
            PartialFrameTimeout = TimeSpan.FromMilliseconds(50)
        };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        server.Start();
        client.Start();

        await Task.Delay(150);

        Assert.That(server.Completion.IsCompleted, Is.False);
        Assert.That(client.Completion.IsCompleted, Is.False);
    }

    [Test]
    public async Task UnknownNotification_ClosesOnlyThatConnection()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        server.Start();

        var header = new byte[RpcProtocolConstants.HeaderSize];
        new RpcFrameHeader(RpcFrameKind.Notification, RpcFrameFlags.None, 0x40001, 0, 0, 0).Write(header);
        await clientStream.WriteAsync(header);
        await clientStream.FlushAsync();

        Assert.ThrowsAsync<RpcProtocolException>(async () => await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task HandshakeTimeout_ClosesConnectionWithoutFirstFrame()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { HandshakeTimeout = TimeSpan.FromMilliseconds(50) };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        server.Start();

        Assert.ThrowsAsync<TimeoutException>(async () => await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task InvokeAsync_UsesExplicitHandlerAndMessagePackPayload()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var testOptions = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, testOptions);
        await using var client = new RpcConnection(clientStream, isServer: false, testOptions);
        server.RegisterRequestHandler<int, int>(0x10001, (value, _) => ValueTask.FromResult(value + 1));
        server.Start();
        client.Start();

        var result = await client.InvokeAsync<int, int>(0x10001, 41).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public async Task SendNotificationAsync_ServerToClient_InvokesRegisteredHandler()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var received = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        client.RegisterNotificationHandler<int>(
            0x10002,
            (value, _) =>
            {
                received.TrySetResult(value);
                return ValueTask.CompletedTask;
            });
        server.Start();
        client.Start();

        await server.SendNotificationAsync(0x10002, 42);

        Assert.That(await received.Task.WaitAsync(TimeSpan.FromSeconds(5)), Is.EqualTo(42));
    }

    [Test]
    public async Task InvokeAsync_HandlerError_EchoesOperationAndKeepsConnectionUsable()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        server.RegisterRequestHandler<int, int>(0x30010, static (_, _) =>
            throw new InvalidOperationException("expected test failure"));
        server.RegisterRequestHandler<int, int>(0x30011, static (request, _) => ValueTask.FromResult(request + 1));
        server.Start();
        client.Start();

        var exception = Assert.ThrowsAsync<RpcRemoteException>(async () =>
            await client.InvokeAsync<int, int>(0x30010, 0).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await client.InvokeAsync<int, int>(0x30011, 41).AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(exception?.Code, Is.EqualTo("handler_error"));
        Assert.That(result, Is.EqualTo(42));
    }

    [Test]
    public async Task InvokeAsync_UnknownOperation_ClosesConnectionAsProtocolViolation()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        server.Start();
        client.Start();

        var invocation = client.InvokeAsync<int, int>(0x30012, 0).AsTask();
        var exception = Assert.ThrowsAsync<RpcProtocolException>(async () =>
            await server.Completion.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.That(exception?.Message, Does.Contain("not registered"));
        Assert.CatchAsync<Exception>(async () => await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Test]
    public async Task PerformHandshakeAsync_RoundTripsGeneratedMessagePackContract()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        await using var server = new RpcConnection(serverStream, isServer: true);
        await using var client = new RpcConnection(clientStream, isServer: false);
        var serverIdentity = new RpcHandshakeIdentity
        {
            AssemblyInformationalVersion = "test-version",
            WireName = "input",
            ProcessId = Environment.ProcessId,
            DesktopSessionId = "test-session"
        };
        server.RegisterRequestHandler<RpcHandshake, RpcHandshakeAck>(
            RpcProtocolConstants.HandshakeOperationId,
            (handshake, _) => ValueTask.FromResult(
                RpcHandshakeValidator.Validate(handshake, ProcessRole.Main, serverIdentity)));
        server.Start();
        client.Start();

        var response = await client.PerformHandshakeAsync(
                new RpcHandshake
                {
                    AssemblyInformationalVersion = "test-version",
                    Role = "main",
                    ProcessId = Environment.ProcessId,
                    DesktopSessionId = "test-session"
                })
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(response.ConnectionNonce, Is.Not.Null);
        Assert.That(Guid.TryParse(response.ConnectionNonce, out _), Is.True);
        Assert.That(response.Accepted, Is.True);
        Assert.That(response.AssemblyInformationalVersion, Is.EqualTo("test-version"));
        Assert.That(response.Role, Is.EqualTo("input"));
        Assert.That(response.DesktopSessionId, Is.EqualTo("test-session"));
        Assert.That(server.ConnectionNonce, Is.EqualTo(response.ConnectionNonce));
        Assert.That(client.ConnectionNonce, Is.EqualTo(response.ConnectionNonce));
    }

    [Test]
    public void ValidateHandshake_DifferentDesktopSession_IsRejected()
    {
        var identity = new RpcHandshakeIdentity
        {
            AssemblyInformationalVersion = "test-version",
            WireName = "input",
            ProcessId = Environment.ProcessId,
            DesktopSessionId = "desktop-1"
        };
        var response = RpcHandshakeValidator.Validate(
            new RpcHandshake
            {
                AssemblyInformationalVersion = "test-version",
                Role = "main",
                ProcessId = Environment.ProcessId,
                DesktopSessionId = "desktop-2"
            },
            ProcessRole.Main,
            identity);

        Assert.That(response.Accepted, Is.False);
        Assert.That(response.RejectionCode, Is.EqualTo("desktop_session_mismatch"));
        Assert.That(response.ConnectionNonce, Is.Null);
    }

    [Test]
    public void ValidateHandshake_ControllerWireName_IsAcceptedWithoutAddingAProcessRole()
    {
        var identity = new RpcHandshakeIdentity
        {
            AssemblyInformationalVersion = "test-version",
            WireName = "main",
            ProcessId = Environment.ProcessId,
            DesktopSessionId = "desktop-1"
        };

        var response = RpcHandshakeValidator.Validate(
            new RpcHandshake
            {
                AssemblyInformationalVersion = "test-version",
                Role = MainHostControlRpcOperations.ControllerWireName,
                ProcessId = Environment.ProcessId,
                DesktopSessionId = "desktop-1"
            },
            MainHostControlRpcOperations.ControllerWireName,
            identity);

        Assert.That(response.Accepted, Is.True);
        Assert.That(response.Role, Is.EqualTo("main"));
        Assert.That(response.ConnectionNonce, Is.Not.Null);
    }

    [Test]
    public void ValidateAcceptedPeer_UnexpectedRole_Throws()
    {
        var mainIdentity = new RpcHandshakeIdentity
        {
            AssemblyInformationalVersion = "test-version",
            WireName = "main",
            ProcessId = Environment.ProcessId,
            DesktopSessionId = "desktop-1"
        };
        var response = new RpcHandshakeAck
        {
            AssemblyInformationalVersion = "test-version",
            Role = "automation",
            ProcessId = Environment.ProcessId + 1,
            DesktopSessionId = "desktop-1",
            ConnectionNonce = Guid.NewGuid().ToString("N"),
            Accepted = true
        };

        Assert.Throws<RpcProtocolException>(() =>
            RpcHandshakeValidator.ValidateAcceptedPeer(response, ProcessRole.Input, mainIdentity));
    }

    [Test]
    public async Task HostLifecycleRpcBinding_RoutesEveryLifecycleMethod()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        var lifecycle = new TestHostLifecycle();
        HostLifecycleRpcBinding.Bind(server, lifecycle);
        server.Start();
        client.Start();

        var proxy = new HostLifecycleRpcClient(client);
        var status = await proxy.GetStatusAsync(new HostStatusRequest());
        var prepare = await proxy.PrepareForUpdateAsync(new PrepareForUpdateRequest());
        var shutdown = await proxy.ShutdownAsync(new ShutdownRequest());

        Assert.That(status.Role, Is.EqualTo("input"));
        Assert.That(prepare.Reason, Is.EqualTo("prepare_for_update"));
        Assert.That(shutdown.Reason, Is.EqualTo("shutdown"));
        Assert.That(lifecycle.CallCount, Is.EqualTo(3));
    }

    [Test]
    public async Task MainHostControlRpcBinding_RoutesStopRequest()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        var control = new TestMainHostControl();
        MainHostControlRpcBinding.Bind(server, control);
        server.Start();
        client.Start();

        var response = await new MainHostControlRpcClient(client)
            .StopHostsAsync(new StopHostsRequest())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(response.Succeeded, Is.True);
        Assert.That(response.InputHostAcknowledged, Is.True);
        Assert.That(response.AutomationHostAcknowledged, Is.True);
        Assert.That(control.CallCount, Is.EqualTo(1));
    }

    [Test]
    public async Task WatchdogRpcBinding_RegistrationHandleOwnsUnregistration()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var options = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, options);
        await using var client = new RpcConnection(clientStream, isServer: false, options);
        var implementation = new TestWatchdogRpc();
        WatchdogRpcBinding.Bind(server, implementation);
        server.Start();
        client.Start();

        var proxy = new WatchdogRpcClient(client);
        var registration = await proxy.RegisterProcessAsync(
            new RegisterWatchdogProcessRequest
            {
                ProcessId = 42,
                SourceProcessHandle = 123
            });
        var release = await proxy.UnregisterProcessAsync(
            new UnregisterWatchdogProcessRequest
            {
                RegistrationHandle = registration.RegistrationHandle,
                KillIfRunning = false
            });

        Assert.That(registration.Registered, Is.True);
        Assert.That(registration.RegistrationHandle, Is.EqualTo(7));
        Assert.That(release.Found, Is.True);
        Assert.That(implementation.ReleasedHandle, Is.EqualTo(7));
        Assert.That(implementation.KillIfRunning, Is.False);
    }

    [Test]
    public async Task RoleHostRunner_ExitsAfterAuthenticatedConnectionDisconnects()
    {
        var endpoint = TestPipeNames.Create();
        using var runnerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            runnerCancellation.Token);

        await using var clientStream = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);

        await using var client = new RpcConnection(clientStream, isServer: false);
        client.Start();

        var handshake = await client.PerformHandshakeAsync(
                new RpcHandshake
                {
                    AssemblyInformationalVersion = RpcRuntimeIdentity.GetAssemblyInformationalVersion(),
                    Role = "main",
                    ProcessId = Environment.ProcessId,
                    DesktopSessionId = RpcRuntimeIdentity.GetDesktopSessionId()
                })
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(handshake.Accepted, Is.True);
        Assert.That(client.ConnectionNonce, Is.Not.Null);

        await client.DisposeAsync();
        var result = await runner.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public async Task RoleHostRunner_RejectedInitialHandshake_ReturnsFailureExitCode()
    {
        var endpoint = TestPipeNames.Create();
        using var runnerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            runnerCancellation.Token);

        await using var clientStream = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);

        await using var client = new RpcConnection(clientStream, isServer: false);
        client.Start();

        var exception = Assert.ThrowsAsync<RpcRemoteException>(async () =>
            await client.PerformHandshakeAsync(
                    new RpcHandshake
                    {
                        AssemblyInformationalVersion = RpcRuntimeIdentity.GetAssemblyInformationalVersion(),
                        Role = "automation",
                        ProcessId = Environment.ProcessId,
                        DesktopSessionId = RpcRuntimeIdentity.GetDesktopSessionId()
                    })
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(5)));
        var result = await runner.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(exception?.Code, Is.EqualTo("role_mismatch"));
        Assert.That(result, Is.EqualTo(2));
    }

    [Test]
    public async Task RoleHostRunner_DuplicateEndpointExitsWithoutTakingOwnership()
    {
        var endpoint = TestPipeNames.Create();
        using var firstCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstRunner = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            firstCancellation.Token);

        await using var firstClient = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await firstClient.ConnectAsync(5000);

        using var secondCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var secondResult = await ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            secondCancellation.Token);

        Assert.That(secondResult, Is.EqualTo(0));

        firstCancellation.Cancel();
        await firstRunner.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Test]
    public async Task RoleHostRunner_ShutdownFlushesAcknowledgmentBeforeExit()
    {
        var endpoint = TestPipeNames.Create();
        using var runnerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var runner = ProcessRoleHostRunner.RunAsync(
            ProcessRole.Input,
            new[] { "--rpc-endpoint", endpoint },
            runnerCancellation.Token);

        await using var clientStream = new NamedPipeClientStream(
            ".",
            endpoint,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);

        await using var client = new RpcConnection(clientStream, isServer: false);
        client.Start();
        await client.PerformHandshakeAsync(
                new RpcHandshake
                {
                    AssemblyInformationalVersion = RpcRuntimeIdentity.GetAssemblyInformationalVersion(),
                    Role = "main",
                    ProcessId = Environment.ProcessId,
                    DesktopSessionId = RpcRuntimeIdentity.GetDesktopSessionId()
                })
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));

        var response = await new HostLifecycleRpcClient(client)
            .ShutdownAsync(new ShutdownRequest())
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(5));
        var result = await runner.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.That(response.Accepted, Is.True);
        Assert.That(response.Reason, Is.EqualTo("shutdown"));
        Assert.That(result, Is.EqualTo(0));
    }

    [Test]
    public async Task InvokeStreamAsync_ValidatesSequenceAndYieldsAllChunks()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var testOptions = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, testOptions);
        await using var client = new RpcConnection(clientStream, isServer: false, testOptions);
        server.RegisterStreamHandler<int, int>(0x20001, CreateStream);
        server.Start();
        client.Start();

        var values = new List<int>();
        await foreach (var value in client.InvokeStreamAsync<int, int>(0x20001, 3).ConfigureAwait(false))
        {
            values.Add(value);
        }

        Assert.That(values, Is.EqualTo(new[] { 3, 4, 5 }));
    }

    [Test]
    public async Task InvokeStreamAsync_DisposingEnumeratorSendsCancellation()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testOptions = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, testOptions);
        await using var client = new RpcConnection(clientStream, isServer: false, testOptions);
        server.RegisterStreamHandler<int, int>(0x20002, (_, cancellationToken) => CancellableStream(cancellationToken));
        server.Start();
        client.Start();

        var enumerator = client.InvokeStreamAsync<int, int>(0x20002, 0).GetAsyncEnumerator();
        Assert.That(await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)), Is.True);
        Assert.That(enumerator.Current, Is.EqualTo(1));

        await enumerator.DisposeAsync();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        async IAsyncEnumerable<int> CancellableStream(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            yield return 1;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        }
    }

    [Test]
    public async Task DisposeAsync_IsIdempotentWhenCalledConcurrently()
    {
        var connection = new RpcConnection(
            new MemoryStream(),
            isServer: false,
            new RpcConnectionOptions { RequireHandshake = false });

        var first = connection.DisposeAsync().AsTask();
        var second = connection.DisposeAsync().AsTask();
        await Task.WhenAll(first, second);

        Assert.That(connection.Completion.IsCompleted, Is.True);
    }

    [Test]
    public async Task InvokeAsync_CancellationIsDeliveredWhileHandlerIsRunning()
    {
        var pipeName = TestPipeNames.Create();
        await using var serverStream = new NamedPipeServerStream(
            pipeName,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        var waitForConnection = serverStream.WaitForConnectionAsync();
        await using var clientStream = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await clientStream.ConnectAsync(5000);
        await waitForConnection;

        var handlerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var testOptions = new RpcConnectionOptions { RequireHandshake = false };
        await using var server = new RpcConnection(serverStream, isServer: true, testOptions);
        await using var client = new RpcConnection(clientStream, isServer: false, testOptions);
        server.RegisterRequestHandler<int, int>(0x30001, async (_, cancellationToken) =>
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
        server.Start();
        client.Start();

        using var cancellation = new CancellationTokenSource();
        var invocation = client.InvokeAsync<int, int>(0x30001, 0, cancellation.Token).AsTask();
        await handlerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.CatchAsync<OperationCanceledException>(async () => await invocation);

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
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

    private sealed class TestHostLifecycle : IHostLifecycleRpc
    {
        public int CallCount { get; private set; }

        public ValueTask<HostStatusResponse> GetStatusAsync(
            HostStatusRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                new HostStatusResponse
                {
                    Role = "input",
                    State = HostProcessState.Connected,
                    ProcessId = Environment.ProcessId,
                    MonotonicTimestamp = Environment.TickCount64
                });
        }

        public ValueTask<HostOperationResponse> PrepareForUpdateAsync(
            PrepareForUpdateRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                new HostOperationResponse
                {
                    Accepted = true,
                    Reason = "prepare_for_update"
                });
        }

        public ValueTask<HostOperationResponse> ShutdownAsync(
            ShutdownRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                new HostOperationResponse
                {
                    Accepted = true,
                    Reason = "shutdown"
                });
        }
    }

    private sealed class TestMainHostControl : IMainHostControlRpc
    {
        public int CallCount { get; private set; }

        public ValueTask<StopHostsResponse> StopHostsAsync(
            StopHostsRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ValueTask.FromResult(
                new StopHostsResponse
                {
                    Succeeded = true,
                    InputHostAcknowledged = true,
                    AutomationHostAcknowledged = true
                });
        }
    }

    private sealed class TestWatchdogRpc : IWatchdogRpc
    {
        public ulong ReleasedHandle { get; private set; }

        public bool KillIfRunning { get; private set; }

        public ValueTask<RegisterWatchdogProcessResponse> RegisterProcessAsync(
            RegisterWatchdogProcessRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new RegisterWatchdogProcessResponse
                {
                    Registered = request.ProcessId == 42,
                    RegistrationHandle = 7
                });

        public ValueTask<UnregisterWatchdogProcessResponse> UnregisterProcessAsync(
            UnregisterWatchdogProcessRequest request,
            CancellationToken cancellationToken = default)
        {
            ReleasedHandle = request.RegistrationHandle;
            KillIfRunning = request.KillIfRunning;
            return ValueTask.FromResult(new UnregisterWatchdogProcessResponse { Found = true });
        }
    }
}
