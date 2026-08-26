using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Diagnostics;

/// <summary>
/// Host-to-Main diagnostic contract. Host roles remain independent from Main's
/// logging packages; Main receives these notifications and writes them through
/// the application logger.
/// </summary>
[RpcContract(HostDiagnosticsRpcOperations.ContractId)]
public interface IHostDiagnosticsRpc
{
    /// <summary>Forwards one structured Host log entry to Main.</summary>
    [RpcNotification(HostDiagnosticsRpcOperations.LogMethodId)]
    ValueTask LogAsync(
        HostLogNotification notification,
        CancellationToken cancellationToken = default);
}