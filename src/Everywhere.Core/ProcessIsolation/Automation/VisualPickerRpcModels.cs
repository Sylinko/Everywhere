using Everywhere.Automation;
using Everywhere.Interop;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Creates one Context-owned interactive visual picker.</summary>
[MessagePackObject]
public sealed partial class BeginVisualPickerRequest
{
    /// <summary>Connection-scoped parent Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Caller-allocated child picker resource ID.</summary>
    [Key(1)]
    public required long PickerId { get; init; }

    /// <summary>Main process whose picker windows must not become selection targets.</summary>
    [Key(2)]
    public required int MainProcessId { get; init; }
}

/// <summary>Moves one interactive picker to a new screen point and selection mode.</summary>
[MessagePackObject]
public sealed partial class UpdateVisualPickerRequest
{
    /// <summary>Connection-scoped parent Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Connection-scoped picker resource ID.</summary>
    [Key(1)]
    public required long PickerId { get; init; }

    /// <summary>Strictly increasing update revision assigned by Main.</summary>
    [Key(2)]
    public required long Revision { get; init; }

    /// <summary>Horizontal screen coordinate in physical pixels.</summary>
    [Key(3)]
    public required int PointX { get; init; }

    /// <summary>Vertical screen coordinate in physical pixels.</summary>
    [Key(4)]
    public required int PointY { get; init; }

    /// <summary>Topological selection mode applied by Host.</summary>
    [Key(5)]
    public required ScreenSelectionMode Mode { get; init; }
}

/// <summary>Contains one revisioned scalar picker observation.</summary>
[MessagePackObject]
public sealed partial class VisualPickerObservation
{
    /// <summary>Update revision represented by this observation.</summary>
    [Key(0)]
    public required long Revision { get; init; }

    /// <summary>Bounded scalar observation of the current candidate.</summary>
    [Key(1)]
    public required AcquireAutomationAnchorResponse Candidate { get; init; }

    /// <summary>Gets the current candidate Snapshot, or <see langword="null" /> when no candidate is available.</summary>
    [IgnoreMember]
    public VisualElementSnapshot? Snapshot => Candidate.IsAvailable ? Candidate.ToSnapshot() : null;

    /// <summary>Gets the provider failure that prevented a current candidate from being observed.</summary>
    [IgnoreMember]
    public VisualElementQueryFailureKind? FailureKind => Candidate.FailureKind;
}

/// <summary>Confirms and transfers a picker's exact current candidate into a remote visual anchor.</summary>
[MessagePackObject]
public sealed partial class ConfirmVisualPickerRequest
{
    /// <summary>Connection-scoped parent Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Connection-scoped picker resource ID.</summary>
    [Key(1)]
    public required long PickerId { get; init; }

    /// <summary>Caller-allocated child anchor resource ID.</summary>
    [Key(2)]
    public required long AnchorId { get; init; }

    /// <summary>Last observation revision accepted by Main.</summary>
    [Key(3)]
    public required long Revision { get; init; }

    /// <summary>Scalar fields requested for the confirmed anchor's initial observation.</summary>
    [Key(4)]
    public required VisualElementFields RequestedFields { get; init; }

    /// <summary>Maximum text-preview length for the confirmed anchor's initial observation.</summary>
    [Key(5)]
    public required int MaxTextCharacters { get; init; }
}