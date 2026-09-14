using Everywhere.Automation;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Creates one remote visual Context with the existing Automation retention policy.</summary>
[MessagePackObject]
public sealed partial class CreateAutomationContextRequest
{
    /// <summary>Caller-allocated resource ID used by both creation and cancellation-safe release.</summary>
    [Key(0)]
    public required long ContextResourceId { get; init; }

    /// <summary>Maximum completed turns retained for historical target lookup.</summary>
    [Key(1)]
    public required int MaximumRetainedTurnCount { get; init; }

    /// <summary>Soft maximum distinct targets retained by completed turns.</summary>
    [Key(2)]
    public required int MaximumRetainedTargetCount { get; init; }
}

/// <summary>Identifies one connection-owned visual Context.</summary>
[MessagePackObject]
public sealed partial class AutomationContextRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }
}

/// <summary>Builds a bounded visual projection from a platform-default root.</summary>
[MessagePackObject]
public sealed partial class BuildDefaultVisualContextRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Topological level selected from the platform default.</summary>
    [Key(1)]
    public required VisualElementResolution Resolution { get; init; }

    /// <summary>Relations observed around the acquired root.</summary>
    [Key(2)]
    public required VisualContextTraverseDirections Directions { get; init; }

    /// <summary>Maximum admitted Snapshot nodes.</summary>
    [Key(3)]
    public required int MaximumNodes { get; init; }

    /// <summary>Approximate token budget for the final projection.</summary>
    [Key(4)]
    public required int TargetTokenBudget { get; init; }
}

/// <summary>Queries a retained integer target in one visual Context.</summary>
[MessagePackObject]
public sealed partial class QueryAutomationTargetRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Agent-visible target ID.</summary>
    [Key(1)]
    public required int TargetId { get; init; }

    /// <summary>Relations observed around the target.</summary>
    [Key(2)]
    public required VisualContextTraverseDirections Directions { get; init; }

    /// <summary>One-based retained-member offset.</summary>
    [Key(3)]
    public required int Offset { get; init; }

    /// <summary>Maximum admitted query nodes.</summary>
    [Key(4)]
    public required int Limit { get; init; }

    /// <summary>Approximate token budget for the final projection.</summary>
    [Key(5)]
    public required int TargetTokenBudget { get; init; }
}

/// <summary>Reads a bounded page of target text.</summary>
[MessagePackObject]
public sealed partial class ReadAutomationTextRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Agent-visible target ID.</summary>
    [Key(1)]
    public required int TargetId { get; init; }

    /// <summary>Zero-based UTF-16 offset.</summary>
    [Key(2)]
    public required int Offset { get; init; }

    /// <summary>Maximum requested UTF-16 character count.</summary>
    [Key(3)]
    public required int Limit { get; init; }
}

/// <summary>Contains final visual text and operation-local publication statistics.</summary>
[MessagePackObject]
public sealed partial class AutomationVisualQueryResponse
{
    /// <summary>Final bounded model-facing content.</summary>
    [Key(0)]
    public required string Content { get; init; }

    /// <summary>Number of targets represented by this operation.</summary>
    [Key(1)]
    public required int RepresentedTargetCount { get; init; }
}

/// <summary>Contains final bounded text-read output.</summary>
[MessagePackObject]
public sealed partial class ReadAutomationTextResponse
{
    /// <summary>Final model-facing text page and continuation metadata.</summary>
    [Key(0)]
    public required string Content { get; init; }
}