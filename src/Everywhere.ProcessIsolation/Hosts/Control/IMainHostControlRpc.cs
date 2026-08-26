using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Control;

/// <summary>
/// Main-owned control contract for the short-lived <c>--hosts-control stop</c>
/// command. The controller uses this endpoint instead of opening a competing
/// primary connection to either Host. A successful response means Main has sent
/// lifecycle shutdown requests to both roles and both role supervisors have
/// released their authenticated connection leases.
/// </summary>
[RpcContract(2)]
public interface IMainHostControlRpc
{
    /// <summary>
    /// Stops the current Host generation and returns an explicit aggregate
    /// confirmation for the Input and Automation roles.
    /// </summary>
    /// <param name="request">The closed stop request; no process or path is supplied by the caller.</param>
    /// <param name="cancellationToken">Cancels the local operation before the response is sent.</param>
    /// <returns>Per-role confirmation and a bounded diagnostic category.</returns>
    [RpcMethod(1)]
    ValueTask<StopHostsResponse> StopHostsAsync(
        StopHostsRequest request,
        CancellationToken cancellationToken = default);
}