using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Role-specific work owned by one authenticated Host connection. Platform
/// projects implement this boundary without constructing the Main application graph.
/// </summary>
public interface IProcessRoleSession : IAsyncDisposable
{
    /// <summary>Registers every role-specific RPC handler before the connection starts.</summary>
    void Bind(RpcConnection connection);

    /// <summary>
    /// Receives Main's validated handshake identity before ordinary role requests
    /// are accepted. Implementations store only the peer facts needed by their
    /// platform work and must not start application work from this callback.
    /// </summary>
    void OnAuthenticated(RpcHandshake peer);

    /// <summary>
    /// Stops accepting role work and enters fail-open cleanup. Implementations
    /// must be idempotent because connection loss and lifecycle RPC may race.
    /// </summary>
    ValueTask BeginDrainingAsync(CancellationToken cancellationToken = default);
}