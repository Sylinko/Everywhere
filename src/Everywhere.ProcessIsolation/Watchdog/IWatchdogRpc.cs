using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>
/// RPC contract owned by the dedicated Watchdog process. A registration is a
/// connection-scoped lease over one captured operating-system process identity;
/// callers subsequently refer to that lease only through its returned handle.
/// </summary>
[RpcContract(WatchdogRpcOperations.ContractId)]
public interface IWatchdogRpc
{
    /// <summary>Captures a process identity and starts monitoring it for Main's lifetime.</summary>
    [RpcMethod(WatchdogRpcOperations.RegisterProcessMethodId)]
    ValueTask<RegisterWatchdogProcessResponse> RegisterProcessAsync(
        RegisterWatchdogProcessRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases one exact registration. The optional termination applies to the
    /// captured process object, never to a process found later by the same PID.
    /// </summary>
    [RpcMethod(WatchdogRpcOperations.UnregisterProcessMethodId)]
    ValueTask<UnregisterWatchdogProcessResponse> UnregisterProcessAsync(
        UnregisterWatchdogProcessRequest request,
        CancellationToken cancellationToken = default);
}