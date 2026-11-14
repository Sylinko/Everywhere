using Avalonia;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// One output's logical placement and native pixel density.
/// </summary>
public sealed record OutputInfo(string Name, PixelRect Bounds, double Scale);

/// <summary>
/// A compositor-owned application surface. Id and Handle are opaque, session-local
/// identities; Handle must never be dereferenced. ClientRect anchors AT-SPI WINDOW
/// coordinates. FrameRect includes server decorations when the backend can observe
/// them; null explicitly means that geometry is unavailable.
/// </summary>
public sealed record CompositorWindowInfo(
    string Id,
    nint Handle,
    int ProcessId,
    string ApplicationId,
    string Title,
    string OutputName,
    PixelRect ClientRect,
    PixelRect? FrameRect,
    bool IsFullscreen)
{
    /// <summary>
    /// Uses client geometry only when the compositor does not expose its frame.
    /// </summary>
    public PixelRect SelectionBounds => FrameRect ?? ClientRect;
}

/// <summary>
/// The strength of the compositor's window-order observation.
/// </summary>
public enum WindowStackingOrderKind
{
    /// <summary>
    /// Authoritative compositor order.
    /// </summary>
    Compositor,

    /// <summary>
    /// Workspace, floating state and focus history estimate; overlapping windows can be ambiguous.
    /// </summary>
    FocusHistoryEstimate,

    /// <summary>
    /// No global window stack can be observed by this backend.
    /// </summary>
    Unavailable,
}

/// <summary>
/// A finite observation used by one element acquisition or relation enumeration.
/// </summary>
public sealed class DesktopSnapshot(IReadOnlyList<OutputInfo> outputs, IReadOnlyList<CompositorWindowInfo> windows)
{
    /// <summary>
    /// Gets the active outputs observed for this snapshot.
    /// </summary>
    public IReadOnlyList<OutputInfo> Outputs { get; } = outputs;

    /// <summary>
    /// Gets the observed windows in the provider's stacking order.
    /// </summary>
    public IReadOnlyList<CompositorWindowInfo> Windows { get; } = windows;

    /// <summary>
    /// Finds the first window below a point, excluding the picker's own process.
    /// </summary>
    public CompositorWindowInfo? WindowAt(PixelPoint point, int excludedProcessId = 0)
    {
        return Windows.FirstOrDefault(window => window.ProcessId != excludedProcessId && window.SelectionBounds.Contains(point));
    }
}