namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>Closed wire identities used by non-role RPC peers.</summary>
public static class RpcPeerNames
{
    /// <summary>Wire identity claimed by the short-lived Hosts controller.</summary>
    public const string HostsControl = "hosts-control";

    /// <summary>Wire identity claimed by the dedicated Watchdog process.</summary>
    public const string Watchdog = "watchdog";
}