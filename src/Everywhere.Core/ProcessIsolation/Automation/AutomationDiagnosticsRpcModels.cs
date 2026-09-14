using Everywhere.Automation;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Requests one bounded structured inspection of a Host-owned visual anchor.</summary>
[MessagePackObject]
public sealed partial class InspectAutomationAnchorRequest
{
    /// <summary>Default maximum number of nodes returned to the diagnostic UI.</summary>
    public const int DefaultMaximumNodes = 1024;

    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Connection-scoped visual anchor resource ID.</summary>
    [Key(1)]
    public required long AnchorId { get; init; }

    /// <summary>Maximum number of visual nodes admitted into the diagnostic tree.</summary>
    [Key(2)]
    public required int MaximumNodes { get; init; }
}

/// <summary>Contains one bounded structured visual tree and traversal status.</summary>
[MessagePackObject]
public sealed partial class AutomationVisualTreeResponse
{
    /// <summary>Ordered roots of the observed visual forest.</summary>
    [Key(0)]
    public required AutomationVisualTreeNode[] Roots { get; init; }

    /// <summary>Snapshot-wide traversal status.</summary>
    [Key(1)]
    public required string[] Status { get; init; }
}

/// <summary>Contains one published diagnostic target and its bounded observed children.</summary>
[MessagePackObject]
public sealed partial class AutomationVisualTreeNode
{
    /// <summary>Context-scoped target ID used by later diagnostic operations.</summary>
    [Key(0)]
    public required int TargetId { get; init; }

    /// <summary>Scalar observation captured during tree traversal.</summary>
    [Key(1)]
    public required AcquireAutomationAnchorResponse Observation { get; init; }

    /// <summary>Ordered observed children.</summary>
    [Key(2)]
    public required AutomationVisualTreeNode[] Children { get; init; }

    /// <summary>Node-local traversal status.</summary>
    [Key(3)]
    public required string[] Status { get; init; }

    /// <summary>Gets the scalar observation as the shared Automation Snapshot type.</summary>
    [IgnoreMember]
    public VisualElementSnapshot Snapshot => Observation.ToSnapshot();
}

/// <summary>Requests current scalar fields from one published diagnostic target.</summary>
[MessagePackObject]
public sealed partial class GetAutomationTargetSnapshotRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Context-scoped published target ID.</summary>
    [Key(1)]
    public required int TargetId { get; init; }

    /// <summary>Scalar fields requested from the current native element.</summary>
    [Key(2)]
    public required VisualElementFields RequestedFields { get; init; }

    /// <summary>Maximum text-preview length for this observation.</summary>
    [Key(3)]
    public required int MaxTextCharacters { get; init; }
}

/// <summary>Requests model-facing text for selected diagnostic targets and Host-side file output.</summary>
[MessagePackObject]
public sealed partial class WriteAutomationVisualTreeFileRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Context-scoped published targets written in this order.</summary>
    [Key(1)]
    public required int[] TargetIds { get; init; }

    /// <summary>Approximate token budget applied to each selected root.</summary>
    [Key(2)]
    public required int TargetTokenBudget { get; init; }
}

/// <summary>Returns the local file written by an Automation Host diagnostic operation.</summary>
[MessagePackObject]
public sealed partial class WriteAutomationVisualTreeFileResponse
{
    /// <summary>Absolute path visible to the co-located Main process.</summary>
    [Key(0)]
    public required string FilePath { get; init; }
}