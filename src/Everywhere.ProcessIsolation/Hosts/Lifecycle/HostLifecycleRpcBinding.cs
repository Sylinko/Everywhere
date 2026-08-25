using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Lifecycle;

/// <summary>
/// Hand-written server binder for <see cref="IHostLifecycleRpc"/>. It maps the
/// explicit operation IDs to typed <see cref="RpcConnection"/> handlers without
/// reflection or a generated runtime registry.
/// </summary>
public static class HostLifecycleRpcBinding
{
    /// <summary>
    /// Registers the lifecycle implementation before the connection starts so
    /// the first authenticated request cannot race handler registration.
    /// </summary>
    /// <param name="connection">Unstarted server-side connection.</param>
    /// <param name="implementation">Object receiving lifecycle calls.</param>
    public static void Bind(RpcConnection connection, IHostLifecycleRpc implementation)
    {
        connection.RegisterRequestHandler<HostStatusRequest, HostStatusResponse>(
            HostLifecycleRpcOperations.GetStatus,
            implementation.GetStatusAsync);
        connection.RegisterRequestHandler<PrepareForUpdateRequest, HostOperationResponse>(
            HostLifecycleRpcOperations.PrepareForUpdate,
            implementation.PrepareForUpdateAsync);
        connection.RegisterRequestHandler<ShutdownRequest, HostOperationResponse>(
            HostLifecycleRpcOperations.Shutdown,
            implementation.ShutdownAsync);
    }
}