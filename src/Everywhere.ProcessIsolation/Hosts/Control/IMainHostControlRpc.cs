using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Control;

/// <summary>
/// Main-owned control contract for short-lived <c>--hosts-control stop</c> and
/// <c>--hosts-control shutdown</c> commands. Controllers use this endpoint instead
/// of opening a competing primary connection to either Host. Host stop confirms
/// role cleanup; application shutdown confirms acceptance and requires a separate process wait.
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

    /// <summary>
    /// Accepts normal application shutdown. The response is flushed before Main requests
    /// UI shutdown; the controller must separately wait for the Main process to exit.
    /// </summary>
    [RpcMethod(2)]
    ValueTask<RpcAck> ShutdownApplicationAsync(
        ShutdownApplicationRequest request,
        CancellationToken cancellationToken = default);
}