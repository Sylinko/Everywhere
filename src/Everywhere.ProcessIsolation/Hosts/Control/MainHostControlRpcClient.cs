using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Control;

/// <summary>Typed client for the Main-owned Hosts control endpoint.</summary>
public sealed class MainHostControlRpcClient(RpcConnection connection) : IMainHostControlRpc
{
    /// <summary>Requests an aggregate, acknowledged stop of both Host roles.</summary>
    public ValueTask<StopHostsResponse> StopHostsAsync(StopHostsRequest request, CancellationToken cancellationToken = default) =>
        connection.InvokeAsync<StopHostsRequest, StopHostsResponse>(
            MainHostControlRpcOperations.StopHosts,
            request,
            cancellationToken);
}