namespace Everywhere.ProcessIsolation.Hosts.Control;

/// <summary>
/// Stable operation IDs and handshake identity for the Main-control endpoint.
/// The controller identity is a command-mode wire name, not a
/// <see cref="Everywhere.ProcessIsolation.Roles.ProcessRole"/>.
/// </summary>
public static class MainHostControlRpcOperations
{
    /// <summary>Stable contract number reserved for Main-owned Host control.</summary>
    public const ushort ContractId = 2;

    /// <summary>Method number for the aggregate Host stop request.</summary>
    public const ushort StopHostsMethodId = 1;

    /// <summary>Combined contract/method operation ID for <c>StopHosts</c>.</summary>
    public const uint StopHosts = (ContractId << 16) | StopHostsMethodId;

    /// <summary>Wire role claimed by the short-lived Hosts controller.</summary>
    public const string ControllerWireName = "hosts-control";
}