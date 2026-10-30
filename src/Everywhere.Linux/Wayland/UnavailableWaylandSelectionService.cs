using Avalonia.Media.Imaging;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Linux.Wayland;

/// <summary>
/// Rejects interactive selection before creating any overlay or querying unavailable global geometry.
/// </summary>
public sealed class UnavailableWaylandSelectionService : IScreenSelectionService
{
    /// <inheritdoc />
    public Task<RemoteVisualAnchor?> PickApplicationWindowAsync(IHostedVisualContext visualContext, CancellationToken cancellationToken = default)
    {
        return Task.FromException<RemoteVisualAnchor?>(Unavailable());
    }

    /// <inheritdoc />
    public Task<RemoteVisualAnchor?> PickVisualElementAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<RemoteVisualAnchor?>(Unavailable());
    }

    /// <inheritdoc />
    public Task<Bitmap?> TakeScreenshotAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        return Task.FromException<Bitmap?>(Unavailable());
    }

    private static NotSupportedException Unavailable() => new("Interactive desktop selection has not been ported to this Wayland desktop.");
}