using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Diagnostics;

/// <summary>Hand-written Main-side binder for <see cref="IHostDiagnosticsRpc"/>.</summary>
public static class HostDiagnosticsRpcBinding
{
    /// <summary>Registers the diagnostic notification handler on a Host connection.</summary>
    public static void Bind(RpcConnection connection, IHostDiagnosticsRpc implementation)
    {
        connection.RegisterNotificationHandler<HostLogNotification>(
            HostDiagnosticsRpcOperations.Log,
            implementation.LogAsync);
    }
}

/// <summary>Thin typed sender used by a Host without loading Main's logging graph.</summary>
public sealed class HostDiagnosticsRpcClient(RpcConnection connection) : IHostDiagnosticsRpc
{
    /// <inheritdoc />
    public ValueTask LogAsync(
        HostLogNotification notification,
        CancellationToken cancellationToken = default) =>
        connection.SendNotificationAsync(
            HostDiagnosticsRpcOperations.Log,
            notification,
            cancellationToken);
}