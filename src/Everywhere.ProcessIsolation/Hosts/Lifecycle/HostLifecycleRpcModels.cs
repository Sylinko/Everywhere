using MessagePack;

namespace Everywhere.ProcessIsolation.Hosts.Lifecycle;

/// <summary>Observable lifecycle state of a role Host.</summary>
public enum HostProcessState : byte
{
    /// <summary>The Host is constructing its minimal shell.</summary>
    Starting = 0,

    /// <summary>The endpoint is owned and waiting for the first Main connection.</summary>
    Listening = 1,

    /// <summary>An authenticated Main connection is the Host lifetime lease.</summary>
    Connected = 2,

    /// <summary>The Host rejects new work and is draining connection-owned state.</summary>
    Draining = 3,

    /// <summary>The Host is flushing/disposal-bound shutdown work.</summary>
    Exiting = 4
}

/// <summary>Empty request used to query the state of the current Host connection.</summary>
[MessagePackObject]
public sealed partial class HostStatusRequest;

/// <summary>Snapshot of Host state returned over the authenticated connection.</summary>
[MessagePackObject]
public sealed partial class HostStatusResponse
{
    /// <summary>Stable lower-case role name of the responding Host.</summary>
    [Key(0)]
    public required string Role { get; init; }

    /// <summary>Lifecycle state observed while the request was handled.</summary>
    [Key(1)]
    public required HostProcessState State { get; init; }

    /// <summary>Operating-system process ID of the responding Host.</summary>
    [Key(2)]
    public required long ProcessId { get; init; }

    /// <summary>Local monotonic timestamp used only for diagnostics and age checks.</summary>
    [Key(3)]
    public required long MonotonicTimestamp { get; init; }
}

/// <summary>Requests that a Host stop accepting new work before an update.</summary>
[MessagePackObject]
public sealed partial class PrepareForUpdateRequest
{
    /// <summary>Optional diagnostic reason recorded by the Host.</summary>
    [Key(0)]
    public string? Reason { get; init; }
}

/// <summary>Requests a cooperative Host shutdown.</summary>
[MessagePackObject]
public sealed partial class ShutdownRequest
{
    /// <summary>Whether the caller intends to start a replacement after draining.</summary>
    [Key(0)]
    public bool Restart { get; init; }
}

/// <summary>Result of a lifecycle transition request.</summary>
[MessagePackObject]
public sealed partial class HostOperationResponse
{
    /// <summary>True only when this request performed the first transition to Draining.</summary>
    [Key(0)]
    public required bool Accepted { get; init; }

    /// <summary>Stable short reason or rejection category for diagnostics.</summary>
    [Key(1)]
    public string? Reason { get; init; }
}