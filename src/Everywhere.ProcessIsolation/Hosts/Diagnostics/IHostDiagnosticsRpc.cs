using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Diagnostics;

/// <summary>
/// Host-to-Main diagnostic contract. Host roles remain independent from Main's
/// logging packages; Main receives these notifications and writes them through
/// the application logger.
/// </summary>
[RpcContract(4)]
public interface IHostDiagnosticsRpc
{
    /// <summary>Forwards one structured Host log entry to Main.</summary>
    [RpcMethod(1)]
    ValueTask LogAsync(
        HostLogNotification notification,
        CancellationToken cancellationToken = default);
}