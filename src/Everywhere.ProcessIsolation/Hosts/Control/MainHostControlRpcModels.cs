using MessagePack;

namespace Everywhere.ProcessIsolation.Hosts.Control;

/// <summary>Closed request for stopping the current Host generation.</summary>
[MessagePackObject]
public sealed partial class StopHostsRequest;

/// <summary>
/// Main's explicit stop confirmation. A controller may report success only when
/// both role supervisors have acknowledged or confirmed that no lease existed.
/// </summary>
[MessagePackObject]
public sealed partial class StopHostsResponse
{
    /// <summary>True when both role results are confirmed.</summary>
    [Key(0)]
    public required bool Succeeded { get; init; }

    /// <summary>Whether the Input role acknowledged shutdown or had no lease.</summary>
    [Key(1)]
    public required bool InputHostAcknowledged { get; init; }

    /// <summary>Whether the Automation role acknowledged shutdown or had no lease.</summary>
    [Key(2)]
    public required bool AutomationHostAcknowledged { get; init; }

    /// <summary>Stable short diagnostic category when the aggregate failed.</summary>
    [Key(3)]
    public string? Reason { get; init; }
}