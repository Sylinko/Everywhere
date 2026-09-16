using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Everywhere.ProcessIsolation.Hosts.Lifecycle;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;
using Microsoft.Win32.SafeHandles;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Minimal headless host shell shared by the isolated process roles. It does not
/// initialize Entrance, dependency injection, Avalonia, or the Core assembly.
/// The endpoint is a single-lease resource: one accepted Main connection owns the
/// Host lifetime, and this runner never re-listens after that connection ends.
/// </summary>
public static partial class ProcessRoleHostRunner
{
    private const uint LabelSecurityInformation = 0x00000010;
    private const uint SeKernelObject = 6;

    private static TimeSpan InitialConnectionTimeout => TimeSpan.FromSeconds(15);
    private static TimeSpan CleanupTimeout => TimeSpan.FromSeconds(5);

    /// <summary>
    /// Starts the minimal role shell, owns its endpoint, authenticates one Main
    /// connection, and performs bounded cleanup. The return code distinguishes
    /// startup/handshake, connection, and cleanup failures for the parent process.
    /// </summary>
    /// <param name="role">The non-Main role hosted by this process.</param>
    /// <param name="args">Role command-line arguments, including an optional endpoint override.</param>
    /// <param name="peerVerifier">Platform policy that authenticates the connected Main process.</param>
    /// <param name="cancellationToken">Stops startup or the active connection.</param>
    public static Task<int> RunAsync(
        ProcessRole role,
        IReadOnlyList<string> args,
        INamedPipePeerVerifier peerVerifier,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(role, args, peerVerifier, null, cancellationToken);

    /// <summary>
    /// Starts the role shell and composes one connection-owned platform session.
    /// The factory runs on the caller's execution thread after endpoint ownership
    /// is established and before the first asynchronous connection wait.
    /// </summary>
    /// <param name="role">The non-Main role hosted by this process.</param>
    /// <param name="args">Role command-line arguments, including an optional endpoint override.</param>
    /// <param name="peerVerifier">Platform policy that authenticates the connected Main process.</param>
    /// <param name="sessionFactory">Creates the role-specific session without resolving Main's service graph.</param>
    /// <param name="cancellationToken">Stops startup or the active connection.</param>
    public static Task<int> RunAsync(
        ProcessRole role,
        IReadOnlyList<string> args,
        INamedPipePeerVerifier peerVerifier,
        Func<IProcessRoleSession> sessionFactory,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(role, args, peerVerifier, sessionFactory, cancellationToken);

    private static async Task<int> RunCoreAsync(
        ProcessRole role,
        IReadOnlyList<string> args,
        INamedPipePeerVerifier peerVerifier,
        Func<IProcessRoleSession>? sessionFactory,
        CancellationToken cancellationToken)
    {
        if (role is ProcessRole.Main)
        {
            throw new ArgumentException("The main role cannot be hosted by ProcessRoleHostRunner.", nameof(role));
        }

        var localIdentity = RpcRuntimeIdentity.CreateCurrent(role);
        var configuredEndpoint = ProcessRoleCommandLine.ParseHostEndpointOverride(role, args);
        var endpoint = configuredEndpoint ?? ProcessRoleNames.GetDefaultEndpoint(role, localIdentity.DesktopSessionId);

        Console.Out.WriteLine($"Everywhere role={ProcessRoleNames.ToWireName(role)} endpoint={endpoint}");

        EndpointOwnershipLease? ownership = null;
        NamedPipeServerStream? server = null;
        RpcConnection? connection = null;
        IProcessRoleSession? session = null;
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

                session = sessionFactory?.Invoke();

                using var connectionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionDeadline.CancelAfter(InitialConnectionTimeout);
                try
                {
                    await server.WaitForConnectionAsync(connectionDeadline.Token).ConfigureAwait(false);
                    peerVerifier.VerifyClient(server);
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
                session?.Bind(connection);
                lifecycle = new RoleHostLifecycle(role, connection, session);
                lifecycle.SetListening();
                connection.RegisterRequestHandler<RpcHandshake, RpcHandshakeAck>(
                    RpcProtocolConstants.HandshakeOperationId,
                    (handshake, _) =>
                    {
                        var response = RpcHandshakeValidator.Validate(handshake, ProcessRole.Main, localIdentity);
                        if (response.Accepted)
                        {
                            session?.OnAuthenticated(handshake);
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
            if (session is not null && !await DrainWithinDeadlineAsync(session).ConfigureAwait(false))
            {
                exitCode = 3;
            }
            lifecycle?.SetExiting();

            if (session is not null && !await DisposeWithinDeadlineAsync(session, "role session").ConfigureAwait(false))
            {
                exitCode = 3;
            }

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

    /// <summary>Bounds role-specific fail-open cleanup independently of transport disposal.</summary>
    private static async Task<bool> DrainWithinDeadlineAsync(IProcessRoleSession session)
    {
        using var deadline = new CancellationTokenSource(CleanupTimeout);
        try
        {
            await session.BeginDrainingAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            await Console.Error.WriteLineAsync(
                $"Everywhere host cleanup exceeded {CleanupTimeout.TotalSeconds:0} seconds while draining the role session.");
            return false;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"Everywhere host cleanup failed while draining the role session: {exception.Message}");
            return false;
        }
    }

    /// <summary>
    /// Creates the one-client endpoint. Windows uses the OS first-instance flag and
    /// an explicit current-user security descriptor whose medium integrity label
    /// permits the ordinary Main process to connect to an elevated Host. Other
    /// platforms use a separate file lease because the named-pipe implementation
    /// does not expose equivalent ownership semantics.
    /// </summary>
    private static NamedPipeServerStream CreateServer(string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateWindowsServer(endpoint);
        }

        return new NamedPipeServerStream(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsServer(string endpoint)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var userSid = identity.User ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var userSidValue = userSid.Value;
        var security = new PipeSecurity();

        // CurrentUserOnly inherits the elevated creator's integrity label and blocks
        // the filtered-token Main process before RPC authentication can run. Keep the
        // same user-only DACL, then set only LABEL_SECURITY_INFORMATION through a
        // WRITE_OWNER handle. Supplying the label as a complete SACL at creation time
        // instead requires SeSecurityPrivilege, which ordinary processes do not hold.
        // Let Windows assign the owner and primary group from the creator token.
        // Explicitly assigning the user SID as the primary group can require a
        // privilege that filtered and ordinary user tokens do not hold.
        security.SetSecurityDescriptorSddlForm($"D:P(A;;GA;;;{userSidValue})");

        var server = NamedPipeServerStreamAcl.Create(
            endpoint,
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            0,
            0,
            security,
            HandleInheritability.None,
            PipeAccessRights.TakeOwnership);
        try
        {
            SetMediumIntegrityLabel(server.SafePipeHandle);
            return server;
        }
        catch
        {
            server.Dispose();
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void SetMediumIntegrityLabel(SafePipeHandle pipeHandle)
    {
        var descriptor = new RawSecurityDescriptor("S:(ML;;NW;;;ME)");
        var systemAcl = descriptor.SystemAcl ?? throw new InvalidOperationException("The medium-integrity pipe label is invalid.");
        var acl = new byte[systemAcl.BinaryLength];
        systemAcl.GetBinaryForm(acl, 0);

        fixed (byte* aclPointer = acl)
        {
            var error = SetSecurityInfo(pipeHandle, SeKernelObject, LabelSecurityInformation, 0, 0, 0, (nint)aclPointer);
            if (error != 0)
            {
                throw new Win32Exception((int)error, "The Host pipe integrity label could not be applied.");
            }
        }
    }

    [LibraryImport("advapi32.dll")]
    private static partial uint SetSecurityInfo(
        SafePipeHandle handle,
        uint objectType,
        uint securityInfo,
        nint owner,
        nint group,
        nint dacl,
        nint sacl);

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
    private sealed class RoleHostLifecycle(ProcessRole role, RpcConnection connection, IProcessRoleSession? session) : IHostLifecycleRpc
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
            TransitionAsync("prepare_for_update", cancellationToken);

        public ValueTask<HostOperationResponse> ShutdownAsync(ShutdownRequest request, CancellationToken cancellationToken = default) =>
            TransitionAsync("shutdown", cancellationToken);

        // Both PrepareForUpdate and Shutdown share the same idempotent transition:
        // only the first caller receives Accepted=true and arms graceful draining.
        private async ValueTask<HostOperationResponse> TransitionAsync(string reason, CancellationToken cancellationToken)
        {
            var accepted = TryBeginDraining();
            if (accepted)
            {
                try
                {
                    if (session is not null)
                    {
                        await session.BeginDrainingAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    connection.RequestGracefulShutdown();
                }
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