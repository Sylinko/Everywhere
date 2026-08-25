using System.IO.Pipes;
using Everywhere.ProcessIsolation.Hosts.Lifecycle;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Minimal headless host shell used during Phase 1. It intentionally does not
/// initialize Entrance, dependency injection, Avalonia, or the Core assembly.
/// The endpoint is a single-lease resource: one accepted Main connection owns the
/// Host lifetime, and this runner never re-listens after that connection ends.
/// </summary>
public static class ProcessRoleHostRunner
{
    private static readonly TimeSpan InitialConnectionTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts the minimal role shell, owns its endpoint, authenticates one Main
    /// connection, and performs bounded cleanup. The return code distinguishes
    /// startup/handshake, connection, and cleanup failures for the parent process.
    /// </summary>
    /// <param name="role">The non-Main role hosted by this process.</param>
    /// <param name="args">Role command-line arguments, including an optional endpoint override.</param>
    /// <param name="cancellationToken">Stops startup or the active connection.</param>
    public static async Task<int> RunAsync(ProcessRole role, IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        if (role is ProcessRole.Main)
        {
            throw new ArgumentException("The main role cannot be hosted by ProcessRoleHostRunner.", nameof(role));
        }

        var localIdentity = RpcRuntimeIdentity.CreateCurrent(role);
        var configuredEndpoint = ProcessRoleCommandLine.ParseHostEndpointOverride(role, args);
        var endpoint = configuredEndpoint ?? ProcessRoleNames.GetDefaultEndpoint(role, localIdentity.DesktopSessionId);

        await Console.Out.WriteLineAsync($"Everywhere role={ProcessRoleNames.ToWireName(role)} endpoint={endpoint}");

        EndpointOwnershipLease? ownership = null;
        NamedPipeServerStream? server = null;
        RpcConnection? connection = null;
        RoleHostLifecycle? lifecycle = null;
        var exitCode = 0;

        try
        {
            do
            {
                if (!OperatingSystem.IsWindows())
                {
                    ownership = EndpointOwnershipLease.TryAcquire(endpoint);
                    if (ownership is null)
                    {
                        await Console.Error.WriteLineAsync(
                            $"Everywhere role={ProcessRoleNames.ToWireName(role)} did not acquire endpoint ownership.");
                        break;
                    }
                }

                try
                {
                    server = CreateServer(endpoint);
                }
                catch (IOException exception)
                {
                    await Console.Error.WriteLineAsync(
                        $"Everywhere role={ProcessRoleNames.ToWireName(role)} endpoint ownership is already held: {exception.Message}");
                    exitCode = 0;
                    break;
                }

                using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionDeadline.CancelAfter(InitialConnectionTimeout);
                try
                {
                    await server.WaitForConnectionAsync(connectionDeadline.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    exitCode = 0;
                    break;
                }
                catch (OperationCanceledException)
                {
                    await Console.Error.WriteLineAsync(
                        $"Everywhere role={ProcessRoleNames.ToWireName(role)} did not receive an authenticated connection within {InitialConnectionTimeout.TotalSeconds:0} seconds.");
                    exitCode = 2;
                    break;
                }

                connection = new RpcConnection(server, isServer: true);
                server = null;
                lifecycle = new RoleHostLifecycle(role, connection);
                lifecycle.SetListening();
                connection.RegisterRequestHandler<RpcHandshake, RpcHandshakeAck>(
                    RpcProtocolConstants.HandshakeOperationId,
                    (handshake, _) =>
                    {
                        var response = RpcHandshakeValidator.Validate(handshake, ProcessRole.Main, localIdentity);
                        if (response.Accepted)
                        {
                            lifecycle.SetConnected();
                        }
                        else
                        {
                            lifecycle.RequestGracefulShutdown();
                        }

                        return ValueTask.FromResult(response);
                    });
                HostLifecycleRpcBinding.Bind(connection, lifecycle);
                connection.Start(cancellationToken);

                var startupTimeoutTask = Task.Delay(InitialConnectionTimeout, connectionDeadline.Token);
                var completedTask = connection.Completion;
                var firstCompletedTask = await Task.WhenAny(completedTask, lifecycle.Authenticated, startupTimeoutTask).ConfigureAwait(false);

                if (cancellationToken.IsCancellationRequested)
                {
                    exitCode = 0;
                    break;
                }

                if (firstCompletedTask == startupTimeoutTask)
                {
                    await Console.Error.WriteLineAsync(
                        $"Everywhere role={ProcessRoleNames.ToWireName(role)} did not complete its first handshake within {InitialConnectionTimeout.TotalSeconds:0} seconds.");
                    exitCode = 2;
                    break;
                }

                try
                {
                    await completedTask.ConfigureAwait(false);
                    if (!lifecycle.IsAuthenticated)
                    {
                        exitCode = 2;
                    }
                }
                catch (Exception exception)
                {
                    await Console.Error.WriteLineAsync($"Everywhere role={ProcessRoleNames.ToWireName(role)} connection ended: {exception.Message}");
                    exitCode = lifecycle.IsAuthenticated ? 1 : 2;
                }

                lifecycle.BeginDraining();
            }
            while (false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            exitCode = 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Everywhere role={ProcessRoleNames.ToWireName(role)} failed: {exception.Message}");
            exitCode = 1;
        }
        finally
        {
            lifecycle?.BeginDraining();
            lifecycle?.SetExiting();

            if (connection is not null && !await DisposeWithinDeadlineAsync(connection, "RPC connection").ConfigureAwait(false))
            {
                exitCode = 3;
            }

            if (server is not null && !await DisposeWithinDeadlineAsync(server, "RPC endpoint").ConfigureAwait(false))
            {
                exitCode = 3;
            }

            ownership?.Dispose();
        }

        return exitCode;
    }

    /// <summary>
    /// Creates the one-client endpoint. Windows uses the OS first-instance flag and
    /// current-user ACL; other platforms use a separate file lease because the
    /// named-pipe implementation does not expose equivalent ownership semantics.
    /// </summary>
    private static NamedPipeServerStream CreateServer(string endpoint)
    {
        var options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
        if (OperatingSystem.IsWindows())
        {
            options |= PipeOptions.FirstPipeInstance;
        }

        return new NamedPipeServerStream(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            options);
    }

    /// <summary>Runs cleanup with a hard local deadline so a wedged transport cannot keep a Host alive forever.</summary>
    private static async Task<bool> DisposeWithinDeadlineAsync(IAsyncDisposable disposable, string description)
    {
        try
        {
            await disposable.DisposeAsync().AsTask().WaitAsync(CleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            await Console.Error.WriteLineAsync(
                $"Everywhere host cleanup exceeded {CleanupTimeout.TotalSeconds:0} seconds while disposing {description}.");
            return false;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Everywhere host cleanup failed while disposing {description}: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Owns the small state machine used by the shell and lifecycle RPC handlers.
    /// State changes are monotonic; the authenticated connection is the lease that
    /// allows the shell to report Connected and request graceful draining.
    /// </summary>
    private sealed class RoleHostLifecycle(ProcessRole role, RpcConnection connection) : IHostLifecycleRpc
    {
        /// <summary>Current lifecycle state read atomically by status requests.</summary>
        private HostProcessState State => (HostProcessState)Volatile.Read(ref _state);

        /// <summary>Completes after the handshake handler accepts Main.</summary>
        public Task Authenticated => _authenticated.Task;

        /// <summary>Whether the handshake has been accepted.</summary>
        public bool IsAuthenticated => _authenticated.Task.IsCompletedSuccessfully;

        private readonly TaskCompletionSource _authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state = (int)HostProcessState.Starting;

        /// <summary>Moves the shell from Starting to Listening.</summary>
        public void SetListening() => Volatile.Write(ref _state, (int)HostProcessState.Listening);

        /// <summary>Marks the lease Connected and releases the first-handshake waiter.</summary>
        public void SetConnected()
        {
            Volatile.Write(ref _state, (int)HostProcessState.Connected);
            _authenticated.TrySetResult();
        }

        /// <summary>Begins monotonic draining; repeated calls are harmless.</summary>
        public void BeginDraining() => TryBeginDraining();

        /// <summary>Marks final cleanup after the connection and endpoint are closed.</summary>
        public void SetExiting() => Volatile.Write(ref _state, (int)HostProcessState.Exiting);

        /// <summary>Arms the connection's writer to close after queued responses drain.</summary>
        public void RequestGracefulShutdown() => connection.RequestGracefulShutdown();

        // Status is deliberately a snapshot; it does not acquire a long-lived lock
        // over the connection or block the state machine.
        public ValueTask<HostStatusResponse> GetStatusAsync(HostStatusRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                new HostStatusResponse
                {
                    Role = ProcessRoleNames.ToWireName(role),
                    State = State,
                    ProcessId = Environment.ProcessId,
                    MonotonicTimestamp = Environment.TickCount64
                });

        public ValueTask<HostOperationResponse> PrepareForUpdateAsync(
            PrepareForUpdateRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Transition("prepare_for_update"));

        public ValueTask<HostOperationResponse> ShutdownAsync(ShutdownRequest request, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Transition("shutdown"));

        // Both PrepareForUpdate and Shutdown share the same idempotent transition:
        // only the first caller receives Accepted=true and arms graceful draining.
        private HostOperationResponse Transition(string reason)
        {
            var accepted = TryBeginDraining();
            if (accepted)
            {
                connection.RequestGracefulShutdown();
            }

            return new HostOperationResponse
            {
                Accepted = accepted,
                Reason = accepted ? reason : "already_draining"
            };
        }

        private bool TryBeginDraining()
        {
            while (true)
            {
                var current = State;
                if (current is HostProcessState.Draining or HostProcessState.Exiting)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref _state, (int)HostProcessState.Draining, (int)current) == (int)current)
                {
                    return true;
                }
            }
        }
    }
}