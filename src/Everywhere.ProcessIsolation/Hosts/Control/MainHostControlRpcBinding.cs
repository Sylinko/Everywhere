using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Hosts.Control;

/// <summary>
/// Hand-written binder for the Main-control contract. Runtime reflection is not
/// used; a future source generator may emit the same registration code.
/// </summary>
public static class MainHostControlRpcBinding
{
    /// <summary>Registers the closed stop operation before the connection starts.</summary>
    public static void Bind(RpcConnection connection, IMainHostControlRpc implementation)
    {
        connection.RegisterRequestHandler<StopHostsRequest, StopHostsResponse>(
            MainHostControlRpcOperations.StopHosts,
            implementation.StopHostsAsync);
    }
}