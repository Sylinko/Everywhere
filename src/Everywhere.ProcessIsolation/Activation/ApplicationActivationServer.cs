using System.IO.Pipes;
using System.Threading.Channels;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Activation;

/// <summary>
/// Main's early activation endpoint and bounded delivery queue. The listening handle
/// remains owned across short RPC sessions, so there is no single-instance ownership gap.
/// </summary>
public sealed class ApplicationActivationServer : IAsyncDisposable, IApplicationActivationRpc
{
    private static TimeSpan SessionTimeout => TimeSpan.FromSeconds(10);

    private readonly NamedPipeServerStream _server;
    private readonly EndpointOwnershipLease? _ownership;
    private readonly INamedPipePeerVerifier _peerVerifier;
    private readonly RpcHandshakeIdentity _identity = RpcRuntimeIdentity.CreateCurrent(ProcessRole.Main);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Lock _disposeGate = new();
    private readonly Channel<ApplicationActivationRequest> _pending = Channel.CreateBounded<ApplicationActivationRequest>(
        new BoundedChannelOptions(32) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly Task _runTask;
    private Task? _disposeTask;
    private Task? _stopTask;

    private ApplicationActivationServer(NamedPipeServerStream server, EndpointOwnershipLease? ownership, INamedPipePeerVerifier peerVerifier)
    {
        _server = server;
        _ownership = ownership;
        _peerVerifier = peerVerifier;
        _runTask = RunAsync();
    }

    /// <summary>
    /// Claims Main's endpoint before initializing any application services. Null means
    /// a competing owner or inaccessible endpoint; callers may forward but must not start another Main.
    /// </summary>
    public static ApplicationActivationServer? TryCreate(string endpoint, INamedPipePeerVerifier peerVerifier)
    {
        EndpointOwnershipLease? ownership = null;
        if (!OperatingSystem.IsWindows())
        {
            ownership = EndpointOwnershipLease.TryAcquire(endpoint);
            if (ownership is null) return null;
        }

        try
        {
            return new ApplicationActivationServer(NamedPipeEndpoint.CreateServer(endpoint), ownership, peerVerifier);
        }
        catch (UnauthorizedAccessException) when (OperatingSystem.IsWindows())
        {
            ownership?.Dispose();
            return null;
        }
        catch (IOException exception) when (OperatingSystem.IsWindows() && (exception.HResult & 0xffff) is 5 or 231)
        {
            ownership?.Dispose();
            return null;
        }
        catch
        {
            ownership?.Dispose();
            throw;
        }
    }

    /// <summary>Transfers ownership of a native or initial activation through the same validated queue as RPC.</summary>
    public ApplicationActivationStatus Enqueue(ApplicationActivationRequest request)
    {
        if (!request.IsValid) return ApplicationActivationStatus.InvalidRequest;
        lock (_disposeGate)
        {
            if (_stopTask is not null) return ApplicationActivationStatus.ShuttingDown;
            return _pending.Writer.TryWrite(request) ? ApplicationActivationStatus.Accepted : ApplicationActivationStatus.Busy;
        }
    }

    /// <summary>Consumes queued activations once Main's UI and application recipients are ready.</summary>
    public IAsyncEnumerable<ApplicationActivationRequest> ReadPendingAsync(CancellationToken cancellationToken) =>
        _pending.Reader.ReadAllAsync(cancellationToken);

    /// <inheritdoc />
    public ValueTask<ApplicationActivationResponse> ActivateAsync(ApplicationActivationRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ApplicationActivationResponse { Status = Enqueue(request) });
    }

    /// <summary>Stops delivery and sessions while retaining the endpoint claim through Main's remaining cleanup.</summary>
    public Task StopAsync()
    {
        lock (_disposeGate)
        {
            _pending.Writer.TryComplete();
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _runTask.ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await _server.DisposeAsync().ConfigureAwait(false);
            _ownership?.Dispose();
            _lifetime.Dispose();
        }
    }

    private async Task RunAsync()
    {
        Exception? failure = null;
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                await _server.WaitForConnectionAsync(_lifetime.Token).ConfigureAwait(false);
                try
                {
                    _peerVerifier.VerifyClient(_server);
                    await HandleConnectionAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (!_lifetime.IsCancellationRequested)
                {
                    // Never log payloads or peer-supplied exception messages containing callback secrets.
                    await Console.Error.WriteLineAsync($"Application activation session failed: {exception.GetType().Name}.");
                }
                finally
                {
                    _server.Disconnect();
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            _pending.Writer.TryComplete(failure);
        }
    }

    private async Task HandleConnectionAsync()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        deadline.CancelAfter(SessionTimeout);
        await using var connection = new RpcConnection(
            _server,
            isServer: true,
            new RpcConnectionOptions
            {
                LeaveOpen = true,
                MaximumFramePayloadBytes = 64 * 1024,
                MaximumQueuedFrames = 4
            });
        connection.RegisterRequestHandler<RpcHandshake, RpcHandshakeAck>(
            RpcProtocolConstants.HandshakeOperationId,
            (handshake, _) => ValueTask.FromResult(RpcHandshakeValidator.Validate(handshake, RpcPeerNames.ApplicationActivation, _identity)));
        ApplicationActivationRpcBinding.Bind(connection, this);
        connection.Start(deadline.Token);
        await connection.Completion.ConfigureAwait(false);
    }
}