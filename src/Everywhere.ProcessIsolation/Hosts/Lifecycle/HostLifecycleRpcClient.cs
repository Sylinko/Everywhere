using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Lifecycle;

/// <summary>
/// Hand-written client generated from <see cref="IHostLifecycleRpc"/>. This
/// proxy is intentionally thin: serialization, correlation IDs, cancellation,
/// queue limits, and the connection nonce remain owned by <see cref="RpcConnection"/>.
/// </summary>
public sealed class HostLifecycleRpcClient(RpcConnection connection) : IHostLifecycleRpc
{
    /// <summary>Queries the connected Host's current lifecycle snapshot.</summary>
    public ValueTask<HostStatusResponse> GetStatusAsync(HostStatusRequest request, CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<HostStatusRequest, HostStatusResponse>(
            HostLifecycleRpcOperations.GetStatus,
            request,
            cancellationToken);

    /// <summary>Requests idempotent update draining on the connected Host.</summary>
    public ValueTask<HostOperationResponse> PrepareForUpdateAsync(PrepareForUpdateRequest request, CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<PrepareForUpdateRequest, HostOperationResponse>(
            HostLifecycleRpcOperations.PrepareForUpdate,
            request,
            cancellationToken);

    /// <summary>Requests idempotent cooperative shutdown on the connected Host.</summary>
    public ValueTask<HostOperationResponse> ShutdownAsync(ShutdownRequest request, CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<ShutdownRequest, HostOperationResponse>(
            HostLifecycleRpcOperations.Shutdown,
            request,
            cancellationToken);
}