using Avalonia;

namespace Everywhere.Linux.Desktop;

/// <summary>
/// Observes an explicitly selected desktop session. Geometry is in global logical
/// desktop units, independent of output pixel density. Implementations must not
/// capture Avalonia's synchronization context: Automation queries are synchronous.
/// </summary>
public interface ICompositorStateProvider
{
    /// <summary>
    /// Scopes opaque window identities to this compositor session.
    /// </summary>
    string InstanceId { get; }

    /// <summary>
    /// Whether window order is reported by the compositor or estimated by the backend.
    /// </summary>
    WindowStackingOrderKind StackingOrderKind { get; }

    /// <summary>
    /// Returns active independent outputs; mirrored sinks do not create another desktop space.
    /// </summary>
    ValueTask<IReadOnlyList<OutputInfo>> GetOutputsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the pointer in global logical desktop coordinates.
    /// </summary>
    ValueTask<PixelPoint> GetGlobalCursorPositionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns mapped windows on visible workspaces, ordered from top to bottom.
    /// Consult StackingOrderKind before treating the order as authoritative.
    /// </summary>
    ValueTask<IReadOnlyList<CompositorWindowInfo>> GetWindowsInStackingOrderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the active application window, or null when no application owns focus.
    /// </summary>
    ValueTask<CompositorWindowInfo?> GetActiveWindowAsync(CancellationToken cancellationToken = default);
}