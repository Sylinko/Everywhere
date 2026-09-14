using System.Diagnostics.CodeAnalysis;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Owns resources created for one RPC connection and handles release-before-registration races.
/// </summary>
public sealed class RpcRemoteResourceRegistry : IRpcResourceReleaseRpc, IAsyncDisposable
{
    /// <summary>Gets the number of currently owned resources.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _resources.Count;
            }
        }
    }

    private readonly Lock _gate = new();
    private readonly HashSet<long> _releasedBeforeRegistration = [];
    private readonly Dictionary<long, ResourceEntry> _resources = [];
    private bool _isDisposed;

    /// <summary>Registers the release contract on the connection whose resources this registry owns.</summary>
    public void Bind(RpcConnection connection) => RpcResourceReleaseRpcBinding.Bind(connection, this);

    /// <summary>
    /// Transfers ownership of an asynchronous resource. A prior release disposes it immediately and returns <see langword="false" />.
    /// </summary>
    public async ValueTask<bool> RegisterAsync(long resourceId, IAsyncDisposable resource)
    {
        var entry = new ResourceEntry(resource, resource.DisposeAsync);
        bool shouldDispose;
        try
        {
            shouldDispose = RegisterCore(resourceId, entry);
        }
        catch
        {
            await resource.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (shouldDispose) await resource.DisposeAsync().ConfigureAwait(false);
        return !shouldDispose;
    }

    /// <summary>Finds one live connection-owned resource by its exact runtime type.</summary>
    public bool TryGet<T>(long resourceId, [NotNullWhen(true)] out T? resource) where T : class
    {
        lock (_gate)
        {
            if (!_isDisposed && _resources.TryGetValue(resourceId, out var entry) && entry.Value is T typedResource)
            {
                resource = typedResource;
                return true;
            }
        }

        resource = null;
        return false;
    }

    /// <summary>
    /// Removes a registered release token after its resource ownership has already been consumed by another protocol operation.
    /// </summary>
    /// <returns><see langword="true" /> when a live registration was removed.</returns>
    public bool Consume(long resourceId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceId);
        lock (_gate)
        {
            return !_isDisposed && _resources.Remove(resourceId);
        }
    }

    /// <inheritdoc />
    public async ValueTask<RpcAck> ReleaseResourceAsync(RpcResourceReleaseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.ResourceId);
        ResourceEntry? entry;
        lock (_gate)
        {
            if (_isDisposed) return default;
            if (!_resources.Remove(request.ResourceId, out entry)) _releasedBeforeRegistration.Add(request.ResourceId);
        }

        if (entry is not null) await entry.Release().ConfigureAwait(false);
        return default;
    }

    /// <summary>Releases every resource still owned by the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        ResourceEntry[] resources;
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            resources = [.. _resources.Values];
            _resources.Clear();
            _releasedBeforeRegistration.Clear();
        }

        List<Exception>? failures = null;
        foreach (var resource in resources)
        {
            try
            {
                await resource.Release().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null) throw new AggregateException("One or more RPC resources failed to release.", failures);
    }

    private bool RegisterCore(long resourceId, ResourceEntry entry)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(resourceId);
        lock (_gate)
        {
            if (_isDisposed || _releasedBeforeRegistration.Remove(resourceId)) return true;
            if (_resources.TryAdd(resourceId, entry)) return false;
        }

        throw new InvalidOperationException($"RPC resource {resourceId} is already registered on this connection.");
    }

    private sealed record ResourceEntry(object Value, Func<ValueTask> Release);
}
