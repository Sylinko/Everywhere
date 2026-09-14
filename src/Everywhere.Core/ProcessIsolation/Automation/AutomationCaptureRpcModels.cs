using MessagePack;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Identifies whether a capture addresses an unpublished anchor or an Agent-visible target.</summary>
public enum AutomationVisualReferenceKind
{
    Anchor,
    Target,
}

/// <summary>Pixel layouts supported by the raw Automation capture stream.</summary>
public enum AutomationCapturePixelFormat
{
    Bgra8888,
    Rgba8888,
    Rgb565,
    Rgb32,
}

/// <summary>Alpha layouts supported by the raw Automation capture stream.</summary>
public enum AutomationCaptureAlphaFormat
{
    Opaque,
    Premultiplied,
    Unpremultiplied,
}

/// <summary>Captures one retained visual reference.</summary>
[MessagePackObject]
public sealed partial class CaptureAutomationVisualRequest
{
    /// <summary>Connection-scoped Context resource ID.</summary>
    [Key(0)]
    public required long ContextId { get; init; }

    /// <summary>Kind of visual reference addressed by <see cref="ReferenceId" />.</summary>
    [Key(1)]
    public required AutomationVisualReferenceKind ReferenceKind { get; init; }

    /// <summary>Connection-scoped anchor ID or positive Agent target ID.</summary>
    [Key(2)]
    public required long ReferenceId { get; init; }
}

/// <summary>Base item in a capture stream. The header is followed by zero or more ordered pixel chunks.</summary>
[MessagePackObject]
[Union(0, typeof(AutomationCaptureHeader))]
[Union(1, typeof(AutomationCaptureChunk))]
public abstract partial class AutomationCaptureFrame;

/// <summary>Describes the raw bitmap that follows in the capture stream.</summary>
[MessagePackObject]
public sealed partial class AutomationCaptureHeader : AutomationCaptureFrame
{
    /// <summary>Represented desktop bounds X coordinate.</summary>
    [Key(0)]
    public required int BoundsX { get; init; }

    /// <summary>Represented desktop bounds Y coordinate.</summary>
    [Key(1)]
    public required int BoundsY { get; init; }

    /// <summary>Represented desktop bounds width.</summary>
    [Key(2)]
    public required int BoundsWidth { get; init; }

    /// <summary>Represented desktop bounds height.</summary>
    [Key(3)]
    public required int BoundsHeight { get; init; }

    /// <summary>Actual bitmap width in pixels.</summary>
    [Key(4)]
    public required int PixelWidth { get; init; }

    /// <summary>Actual bitmap height in pixels.</summary>
    [Key(5)]
    public required int PixelHeight { get; init; }

    /// <summary>Bytes between adjacent bitmap rows.</summary>
    [Key(6)]
    public required int Stride { get; init; }

    /// <summary>Raw pixel layout.</summary>
    [Key(7)]
    public required AutomationCapturePixelFormat PixelFormat { get; init; }

    /// <summary>Raw alpha layout.</summary>
    [Key(8)]
    public required AutomationCaptureAlphaFormat AlphaFormat { get; init; }

    /// <summary>Total number of pixel bytes carried by subsequent chunks.</summary>
    [Key(9)]
    public required int DataLength { get; init; }
}

/// <summary>Contains one ordered raw pixel block.</summary>
[MessagePackObject]
public sealed partial class AutomationCaptureChunk : AutomationCaptureFrame
{
    /// <summary>Raw bitmap bytes.</summary>
    [Key(0)]
    public required byte[] Data { get; init; }
}