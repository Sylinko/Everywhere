namespace Everywhere.ProcessIsolation.Hosts.Diagnostics;

/// <summary>Stable operation IDs for diagnostics emitted by every Host role.</summary>
public static class HostDiagnosticsRpcOperations
{
    /// <summary>Contract number reserved for Host-to-Main diagnostics.</summary>
    public const ushort ContractId = 4;

    /// <summary>Method number for one structured log entry.</summary>
    public const ushort LogMethodId = 1;

    /// <summary>Combined operation ID for <c>Log</c>.</summary>
    public const uint Log = (ContractId << 16) | LogMethodId;
}