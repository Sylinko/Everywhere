using Everywhere.Automation;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Acquires one Context-owned visual anchor before it is published to an Agent turn.</summary>
[MessagePackObject]
public sealed partial class AcquireAutomationAnchorRequest
{
    /// <summary>Connection-scoped parent Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Caller-allocated child resource ID.</summary>
    [Key(1)]
    public required long AnchorId { get; init; }

    /// <summary>Platform acquisition mechanism.</summary>
    [Key(2)]
    public required VisualElementLocatorKind LocatorKind { get; init; }

    /// <summary>Horizontal screen coordinate used by a point locator.</summary>
    [Key(3)]
    public required int PointX { get; init; }

    /// <summary>Vertical screen coordinate used by a point locator.</summary>
    [Key(4)]
    public required int PointY { get; init; }

    /// <summary>Native window handle used by a native-window locator.</summary>
    [Key(5)]
    public required long NativeWindowHandle { get; init; }

    /// <summary>Topological result resolved from the locator.</summary>
    [Key(6)]
    public required VisualElementResolution Resolution { get; init; }

    /// <summary>Scalar fields requested for the initial observation.</summary>
    [Key(7)]
    public required VisualElementFields RequestedFields { get; init; }

    /// <summary>Maximum text-preview length for the initial observation.</summary>
    [Key(8)]
    public required int MaxTextCharacters { get; init; }
}

/// <summary>Describes the initial bounded observation of an acquired remote visual anchor.</summary>
[MessagePackObject]
public sealed partial class AcquireAutomationAnchorResponse
{
    /// <summary>Whether the locator resolved a visual element.</summary>
    [Key(0)]
    public required bool IsAvailable { get; init; }

    /// <summary>Stable platform identity inside the Host-owned Context.</summary>
    [Key(1)]
    public required string? ElementId { get; init; }

    /// <summary>Observed semantic type.</summary>
    [Key(2)]
    public required VisualElementType? Type { get; init; }

    /// <summary>Observed element states.</summary>
    [Key(3)]
    public required VisualElementStates? States { get; init; }

    /// <summary>Observed accessibility name.</summary>
    [Key(4)]
    public required string? Name { get; init; }

    /// <summary>Observed bounded text preview.</summary>
    [Key(5)]
    public required string? TextPreview { get; init; }

    /// <summary>Whether more text was observed beyond the preview.</summary>
    [Key(6)]
    public required bool HasMoreText { get; init; }

    /// <summary>Whether the response contains desktop bounds.</summary>
    [Key(7)]
    public required bool HasBounds { get; init; }

    /// <summary>Desktop bounds X coordinate.</summary>
    [Key(8)]
    public required int BoundsX { get; init; }

    /// <summary>Desktop bounds Y coordinate.</summary>
    [Key(9)]
    public required int BoundsY { get; init; }

    /// <summary>Desktop bounds width.</summary>
    [Key(10)]
    public required int BoundsWidth { get; init; }

    /// <summary>Desktop bounds height.</summary>
    [Key(11)]
    public required int BoundsHeight { get; init; }

    /// <summary>Observed owning process ID.</summary>
    [Key(12)]
    public required int? ProcessId { get; init; }

    /// <summary>Observed native window handle.</summary>
    [Key(13)]
    public required long? NativeWindowHandle { get; init; }

    /// <summary>Requested fields returned by the provider.</summary>
    [Key(14)]
    public required VisualElementFields AvailableFields { get; init; }

    /// <summary>Requested fields unavailable from the provider.</summary>
    [Key(15)]
    public required VisualElementFields MissingFields { get; init; }

    /// <summary>Normalized provider-wide failure classification.</summary>
    [Key(16)]
    public required VisualElementQueryFailureKind? FailureKind { get; init; }
}

/// <summary>Requests a fresh bounded scalar observation of one Context-owned visual anchor.</summary>
[MessagePackObject]
public sealed partial class GetAutomationElementSnapshotRequest
{
    /// <summary>Connection-scoped parent Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Connection-scoped visual anchor resource ID.</summary>
    [Key(1)]
    public required long AnchorId { get; init; }

    /// <summary>Scalar fields requested from the current native element.</summary>
    [Key(2)]
    public required VisualElementFields RequestedFields { get; init; }

    /// <summary>Maximum text-preview length for this observation.</summary>
    [Key(3)]
    public required int MaxTextCharacters { get; init; }
}

/// <summary>Moves one pre-publication visual anchor into another Context on the same Host connection.</summary>
[MessagePackObject]
public sealed partial class MoveAutomationAnchorRequest
{
    /// <summary>Context that currently owns the source anchor.</summary>
    [Key(0)]
    public required long SourceContextId { get; init; }

    /// <summary>Anchor whose ownership is consumed when the move commits.</summary>
    [Key(1)]
    public required long SourceAnchorId { get; init; }

    /// <summary>Context that receives an independently owned native reference.</summary>
    [Key(2)]
    public required long DestinationContextId { get; init; }

    /// <summary>Caller-allocated destination anchor resource ID.</summary>
    [Key(3)]
    public required long DestinationAnchorId { get; init; }

    /// <summary>Scalar fields requested for the destination observation.</summary>
    [Key(4)]
    public required VisualElementFields RequestedFields { get; init; }

    /// <summary>Maximum text-preview length for the destination observation.</summary>
    [Key(5)]
    public required int MaxTextCharacters { get; init; }
}

/// <summary>Builds one model-facing visual projection from pre-publication anchors.</summary>
[MessagePackObject]
public sealed partial class BuildAutomationAnchorsRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Connection-scoped child anchor IDs in presentation order.</summary>
    [Key(1)]
    public required long[] AnchorIds { get; init; }

    /// <summary>Relations observed around each anchor.</summary>
    [Key(2)]
    public required VisualContextTraverseDirections Directions { get; init; }

    /// <summary>Maximum admitted Snapshot nodes.</summary>
    [Key(3)]
    public required int MaximumNodes { get; init; }

    /// <summary>Approximate token budget for the final projection.</summary>
    [Key(4)]
    public required int TargetTokenBudget { get; init; }
}

/// <summary>Lists and publishes top-level windows in one Host-owned Context.</summary>
[MessagePackObject]
public sealed partial class ListAutomationWindowsRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Approximate token budget for the final window-list projection.</summary>
    [Key(1)]
    public required int TargetTokenBudget { get; init; }
}