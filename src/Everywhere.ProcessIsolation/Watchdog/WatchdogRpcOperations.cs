namespace Everywhere.ProcessIsolation.Watchdog;

/// <summary>Stable Watchdog contract numbers and handshake identity.</summary>
public static class WatchdogRpcOperations
{
    /// <summary>Wire identity claimed only by the dedicated Watchdog executable.</summary>
    public const string WireName = "watchdog";

    /// <summary>Contract number reserved for Watchdog process leases.</summary>
    public const ushort ContractId = 3;

    /// <summary>Method number for process registration.</summary>
    public const ushort RegisterProcessMethodId = 1;

    /// <summary>Method number for handle-based unregistration.</summary>
    public const ushort UnregisterProcessMethodId = 2;

    /// <summary>Combined operation ID for process registration.</summary>
    public const uint RegisterProcess = (ContractId << 16) | RegisterProcessMethodId;

    /// <summary>Combined operation ID for handle-based unregistration.</summary>
    public const uint UnregisterProcess = (ContractId << 16) | UnregisterProcessMethodId;
}