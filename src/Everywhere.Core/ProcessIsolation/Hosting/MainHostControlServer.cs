using System.IO.Pipes;
using Everywhere.ProcessIsolation.Hosts.Control;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.ProcessIsolation.Roles;
using Serilog;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Main-owned, short-lived-command control endpoint. It accepts sequential
/// controller connections while the UI is alive; it never becomes a Host role
/// endpoint and never launches a process on behalf of a caller.
/// </summary>
public sealed class MainHostControlServer : IAsyncDisposable
{
    private readonly ILogger _logger = Log.ForContext<MainHostControlServer>();
    private readonly HostProcessCoordinator _coordinator;
    private readonly RpcHandshakeIdentity _mainIdentity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _disposeGate = new();
    private readonly Task _runTask;
    private Task? _disposeTask;

    private MainHostControlServer(HostProcessCoordinator coordinator)
    {
        _coordinator = coordinator;
        _runTask = RunAsync();
    }

    /// <summary>Starts the Main-control listener for the current desktop session.</summary>
    public static MainHostControlServer Start(HostProcessCoordinator coordinator) => new(coordinator);

    /// <summary>
    /// Stops accepting controllers and waits for the listener to release its pipe.
    /// Existing Host connections are owned by <see cref="HostProcessCoordinator"/>
    /// and are not implicitly changed by disposing this control endpoint.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _runTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        var endpoint = ProcessRoleNames.GetMainControlEndpoint(_mainIdentity.DesktopSessionId);
        while (!_lifetime.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreateServer(endpoint);
                await server.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                await HandleConnectionAsync(server).ConfigureAwait(false);
                server = null;
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                break;
            }
            catch (IOException exception)
            {
                _logger.Warning(exception, "The Main Hosts-control endpoint could not accept a connection.");
                if (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(100), _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                    {
                    }
                }
            }
            catch (Exception exception)
            {
                _logger.Warning(exception, "The Main Hosts-control connection failed.");
            }
            finally
            {
                if (server is not null)
                {
                    await server.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server)
    {
        await using var connection = new RpcConnection(server, isServer: true);
        var implementation = new MainHostControlRpcImplementation(_coordinator, connection);

        connection.RegisterRequestHandler<RpcHandshake, RpcHandshakeAck>(
            RpcProtocolConstants.HandshakeOperationId,
            (handshake, _) =>
            {
                var response = RpcHandshakeValidator.Validate(
                    handshake,
                    MainHostControlRpcOperations.ControllerWireName,
                    _mainIdentity);
                if (!response.Accepted)
                {
                    // ReSharper disable once AccessToDisposedClosure
                    connection.RequestGracefulShutdown();
                }

                return ValueTask.FromResult(response);
            });
        MainHostControlRpcBinding.Bind(connection, implementation);
        connection.Start(_lifetime.Token);

        try
        {
            await connection.Completion.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _logger.Debug(exception, "The Main Hosts-control connection ended before completion.");
        }
    }

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

    private sealed class MainHostControlRpcImplementation(HostProcessCoordinator coordinator, RpcConnection connection) : IMainHostControlRpc
    {
        public async ValueTask<StopHostsResponse> StopHostsAsync(
            StopHostsRequest request,
            CancellationToken cancellationToken = default)
        {
            var result = await coordinator.StopHostsAsync(cancellationToken).ConfigureAwait(false);
            connection.RequestGracefulShutdown();
            return new StopHostsResponse
            {
                Succeeded = result.Succeeded,
                InputHostAcknowledged = result.InputHostAcknowledged,
                AutomationHostAcknowledged = result.AutomationHostAcknowledged,
                Reason = result.Succeeded ? null : "host_shutdown_not_confirmed"
            };
        }
    }
}