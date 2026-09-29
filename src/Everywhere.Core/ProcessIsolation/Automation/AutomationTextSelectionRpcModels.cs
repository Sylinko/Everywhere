using Everywhere.Automation;
using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Contains the complete policy applied by one native text-selection monitor.</summary>
[MessagePackObject]
public sealed partial class TextSelectionMonitoringConfiguration
{
    /// <summary>Gets whether interactions in fullscreen applications are ignored.</summary>
    [Key(0)]
    public required bool IsFullscreenApplicationExcluded { get; init; }

    /// <summary>Gets application identities ignored by automatic text selection.</summary>
    [Key(1)]
    public required string[] ExcludedApplications { get; init; }

    /// <summary>Creates the default safe monitoring policy.</summary>
    public static TextSelectionMonitoringConfiguration CreateDefault() => new()
    {
        IsFullscreenApplicationExcluded = true,
        ExcludedApplications = [],
    };
}

/// <summary>Describes the acknowledged result of changing native text-selection monitoring.</summary>
[MessagePackObject]
public sealed partial class TextSelectionMonitoringControlResult
{
    /// <summary>Gets whether the requested state was established.</summary>
    [Key(0)]
    public required bool IsSucceeded { get; init; }

    /// <summary>Gets the normalized failure category when the request was rejected.</summary>
    [Key(1)]
    public VisualElementQueryFailureKind? FailureKind { get; init; }

    /// <summary>Gets an optional localized explanation suitable for transient presentation.</summary>
    [Key(2)]
    public IDynamicLocaleKey? Message { get; init; }

    /// <summary>Creates a successful control result.</summary>
    public static TextSelectionMonitoringControlResult Success() => new() { IsSucceeded = true };
}

/// <summary>Starts one connection-owned text-selection monitor.</summary>
[MessagePackObject]
public sealed partial class StartTextSelectionMonitoringRequest
{
    /// <summary>Context that owns source observations and pushed anchors.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Client-allocated monitor resource ID.</summary>
    [Key(1)]
    public required long MonitorId { get; init; }

    /// <summary>Authenticated Main process ID excluded from observation.</summary>
    [Key(2)]
    public required int MainProcessId { get; init; }

    /// <summary>Complete policy snapshot applied before native listening begins.</summary>
    [Key(3)]
    public required TextSelectionMonitoringConfiguration Configuration { get; init; }
}

/// <summary>Stops one addressed connection-owned text-selection monitor.</summary>
[MessagePackObject]
public sealed partial class StopTextSelectionMonitoringRequest
{
    /// <summary>Monitor resource ID allocated by Main.</summary>
    [Key(0)]
    public required long MonitorId { get; init; }
}

/// <summary>Applies a complete policy snapshot to one active text-selection monitor.</summary>
[MessagePackObject]
public sealed partial class UpdateTextSelectionMonitoringRequest
{
    /// <summary>Monitor resource ID allocated by Main.</summary>
    [Key(0)]
    public required long MonitorId { get; init; }

    /// <summary>Complete replacement policy.</summary>
    [Key(1)]
    public required TextSelectionMonitoringConfiguration Configuration { get; init; }
}

/// <summary>Pushes one complete text-selection observation from Automation Host to Main.</summary>
[MessagePackObject]
public sealed partial class TextSelectionObservedNotification
{
    /// <summary>Context that owns the optional anchor.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Monitor resource that produced this observation.</summary>
    [Key(1)]
    public required long MonitorId { get; init; }

    /// <summary>Strictly increasing revision within this monitor.</summary>
    [Key(2)]
    public required long Revision { get; init; }

    /// <summary>Nonempty selected text.</summary>
    [Key(4)]
    public required string Text { get; init; }

    /// <summary>Server-allocated negative Anchor ID, or zero when no source is retained.</summary>
    [Key(5)]
    public required long AnchorId { get; init; }

    /// <summary>Initial source observation paired with <see cref="AnchorId" />.</summary>
    [Key(6)]
    public AcquireAutomationAnchorResponse? Source { get; init; }

    /// <summary>Whether transport or native read limits may have omitted trailing text.</summary>
    [Key(7)]
    public bool IsTextIncomplete { get; init; }
}