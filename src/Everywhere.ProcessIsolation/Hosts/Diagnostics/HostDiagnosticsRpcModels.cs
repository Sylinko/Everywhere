using MessagePack;

namespace Everywhere.ProcessIsolation.Hosts.Diagnostics;

/// <summary>Severity understood by the lightweight Host diagnostics contract.</summary>
public enum HostLogLevel
{
    /// <summary>Detailed information useful while diagnosing Host behavior.</summary>
    Debug,

    /// <summary>Normal Host lifecycle or operation information.</summary>
    Information,

    /// <summary>A recoverable problem or fallback path.</summary>
    Warning,

    /// <summary>An operation failed and may require Host recovery.</summary>
    Error
}

/// <summary>
/// One Host log entry. Exception text is already rendered in the Host because
/// exception objects and their implementation types do not cross RPC.
/// </summary>
[MessagePackObject]
public sealed partial class HostLogNotification
{
    /// <summary>Severity selected by the Host.</summary>
    [Key(0)]
    public required HostLogLevel Level { get; init; }

    /// <summary>Small stable component name such as <c>Win32InputHook</c>.</summary>
    [Key(1)]
    public required string Source { get; init; }

    /// <summary>Human-readable diagnostic message without application data payloads.</summary>
    [Key(2)]
    public required string Message { get; init; }

    /// <summary>Optional rendered exception details.</summary>
    [Key(3)]
    public string? ExceptionText { get; init; }
}