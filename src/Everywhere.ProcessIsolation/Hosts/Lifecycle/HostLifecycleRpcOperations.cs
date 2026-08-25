namespace Everywhere.ProcessIsolation.Hosts.Lifecycle;

/// <summary>
/// Stable operation IDs shared by the headless role shell, clients, and binders.
/// Contract and method numbers are composed into the 32-bit operation ID carried
/// by <see cref="Everywhere.ProcessIsolation.Rpc.RpcConnection"/> frames.
/// </summary>
public static class HostLifecycleRpcOperations
{
    /// <summary>The stable contract number reserved for Host lifecycle calls.</summary>
    public const ushort ContractId = 1;

    /// <summary>Method number for the read-only status query.</summary>
    public const ushort GetStatusMethodId = 1;

    /// <summary>Method number for cooperative update draining.</summary>
    public const ushort PrepareForUpdateMethodId = 2;

    /// <summary>Method number for cooperative process shutdown.</summary>
    public const ushort ShutdownMethodId = 3;

    /// <summary>Combined contract/method operation ID for <c>GetStatus</c>.</summary>
    public const uint GetStatus = (ContractId << 16) | GetStatusMethodId;

    /// <summary>Combined contract/method operation ID for <c>PrepareForUpdate</c>.</summary>
    public const uint PrepareForUpdate = (ContractId << 16) | PrepareForUpdateMethodId;

    /// <summary>Combined contract/method operation ID for <c>Shutdown</c>.</summary>
    public const uint Shutdown = (ContractId << 16) | ShutdownMethodId;
}