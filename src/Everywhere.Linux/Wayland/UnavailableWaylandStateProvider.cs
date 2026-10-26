using Avalonia;
using Everywhere.Linux.Desktop;

namespace Everywhere.Linux.Wayland;

/// <summary>
/// Explicitly marks global desktop observation as unported; never invents positions or window identities.
/// </summary>
public sealed class UnavailableWaylandStateProvider : ICompositorStateProvider
{
    /// <inheritdoc />
    public string InstanceId => "wayland:" + Environment.GetEnvironmentVariable("WAYLAND_DISPLAY");

    /// <inheritdoc />
    public WindowStackingOrderKind StackingOrderKind => WindowStackingOrderKind.Unavailable;

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<OutputInfo>> GetOutputsAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException<IReadOnlyList<OutputInfo>>(Unavailable());
    }

    /// <inheritdoc />
    public ValueTask<PixelPoint> GetGlobalCursorPositionAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException<PixelPoint>(Unavailable());
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<CompositorWindowInfo>> GetWindowsInStackingOrderAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException<IReadOnlyList<CompositorWindowInfo>>(Unavailable());
    }

    /// <inheritdoc />
    public ValueTask<CompositorWindowInfo?> GetActiveWindowAsync(CancellationToken cancellationToken = default)
    {
        return ValueTask.FromException<CompositorWindowInfo?>(Unavailable());
    }

    private static NotSupportedException Unavailable() => new("Global desktop state has not been ported to this Wayland desktop.");
}