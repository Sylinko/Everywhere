using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>Thin typed client for the connection-owned Watchdog contract.</summary>
public sealed class WatchdogRpcClient(RpcConnection connection) : IWatchdogRpc
{
    /// <inheritdoc />
    public ValueTask<RegisterWatchdogProcessResponse> RegisterProcessAsync(
        RegisterWatchdogProcessRequest request,
        CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<RegisterWatchdogProcessRequest, RegisterWatchdogProcessResponse>(
            WatchdogRpcOperations.RegisterProcess,
            request,
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<UnregisterWatchdogProcessResponse> UnregisterProcessAsync(
        UnregisterWatchdogProcessRequest request,
        CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<UnregisterWatchdogProcessRequest, UnregisterWatchdogProcessResponse>(
            WatchdogRpcOperations.UnregisterProcess,
            request,
            cancellationToken);
}