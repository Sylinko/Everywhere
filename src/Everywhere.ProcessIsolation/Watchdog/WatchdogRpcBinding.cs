using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>Hand-written server binder for <see cref="IWatchdogRpc"/>.</summary>
public static class WatchdogRpcBinding
{
    /// <summary>Registers all Watchdog handlers before the connection starts.</summary>
    public static void Bind(RpcConnection connection, IWatchdogRpc implementation)
    {
        connection.RegisterRequestHandler<RegisterWatchdogProcessRequest, RegisterWatchdogProcessResponse>(
            WatchdogRpcOperations.RegisterProcess,
            implementation.RegisterProcessAsync);
        connection.RegisterRequestHandler<UnregisterWatchdogProcessRequest, UnregisterWatchdogProcessResponse>(
            WatchdogRpcOperations.UnregisterProcess,
            implementation.UnregisterProcessAsync);
    }
}