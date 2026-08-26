using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>
/// Main-owned stream of authenticated role connections. Product proxies consume
/// replacements in order and restore only their declarative connection state.
/// </summary>
internal interface IHostConnectionSource
{
    /// <summary>Yields the current connection and each later replacement for one role.</summary>
    IAsyncEnumerable<RpcConnection> WatchConnectionsAsync(
        ProcessRole role,
        CancellationToken cancellationToken = default);
}