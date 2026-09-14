using System.Threading.Channels;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Converts nonblocking SafeHandle releases into acknowledged calls for the lifetime of one originating connection.
/// </summary>
public sealed class RpcSafeHandleReleaseQueue : IRpcSafeHandleReleaser
{
    /// <summary>Gets whether the originating RPC connection has already closed.</summary>
    public bool IsConnectionClosed => _connection.Completion.IsCompleted;

    internal Task Completion { get; }

    private readonly RpcConnection _connection;
    private readonly Channel<long> _releases = Channel.CreateUnbounded<long>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private long _nextResourceId;

    internal RpcSafeHandleReleaseQueue(RpcConnection connection)
    {
        _connection = connection;
        Completion = SendReleasesAsync(new RpcResourceReleaseRpcClient(connection));
    }

    /// <summary>Allocates a positive identifier never reused by this release queue.</summary>
    public long AllocateResourceId()
    {
        var resourceId = Interlocked.Increment(ref _nextResourceId);
        return resourceId > 0 ? resourceId : throw new InvalidOperationException("The RPC resource identifier space was exhausted.");
    }

    /// <inheritdoc />
    public bool TryQueueRelease(long resourceId)
    {
        if (resourceId <= 0) return false;
        if (_connection.Completion.IsCompleted) return true;
        if (_releases.Writer.TryWrite(resourceId)) return true;
        return _connection.Completion.IsCompleted;
    }

    private async Task SendReleasesAsync(IRpcResourceReleaseRpc client)
    {
        try
        {
            while (true)
            {
                var releaseAvailable = _releases.Reader.WaitToReadAsync().AsTask();
                if (await Task.WhenAny(releaseAvailable, _connection.Completion).ConfigureAwait(false) != releaseAvailable)
                {
                    return;
                }

                if (!await releaseAvailable.ConfigureAwait(false)) return;
                while (_releases.Reader.TryRead(out var resourceId))
                {
                    await SendReleaseAsync(client, resourceId).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Stop accepting releases before discarding queued IDs. Once the connection ends,
            // the peer's session teardown owns cleanup of all remaining remote resources.
            _releases.Writer.TryComplete();
            while (_releases.Reader.TryRead(out _))
            {
            }
        }
    }

    private async Task SendReleaseAsync(IRpcResourceReleaseRpc client, long resourceId)
    {
        while (!_connection.Completion.IsCompleted)
        {
            try
            {
                await client.ReleaseResourceAsync(new RpcResourceReleaseRequest { ResourceId = resourceId }).ConfigureAwait(false);
                return;
            }
            catch (RpcRemoteException)
            {
                // The peer received the release but could not finish resource cleanup. Retrying cannot restore that resource.
                return;
            }
            catch when (_connection.Completion.IsCompleted)
            {
                return;
            }
            catch
            {
                // Local outbound backpressure can reject an enqueue before it reaches the peer. Retry while this session remains live.
                if (await Task.WhenAny(Task.Delay(25), _connection.Completion).ConfigureAwait(false) != _connection.Completion) continue;
                return;
            }
        }
    }
}