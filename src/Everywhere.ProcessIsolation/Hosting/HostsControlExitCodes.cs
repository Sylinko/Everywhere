namespace Everywhere.ProcessIsolation.Hosting;

/// <summary>Stable exit codes returned by the short-lived Hosts controller.</summary>
public static class HostsControlExitCodes
{
    /// <summary>The requested operation completed successfully.</summary>
    public const int Success = 0;

    /// <summary>The requested operation failed.</summary>
    public const int Failure = 1;

    /// <summary>The requested operation was unavailable or timed out.</summary>
    public const int Unavailable = 2;

    /// <summary>The operation requires an explicit ownership or safety decision.</summary>
    public const int Conflict = 3;

    /// <summary>Service-mode launch failed, but direct Hosts were launched with reduced capabilities.</summary>
    public const int LimitedFallbackStarted = 10;

    /// <summary>Service-mode launch failed, but direct Hosts were launched with equivalent capabilities.</summary>
    public const int EquivalentFallbackStarted = 11;
}