using Avalonia.Media.Imaging;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides the macOS interactive element and screenshot selection experience.
/// </summary>
public sealed partial class MacScreenSelectionService(IWindowHelper windowHelper) : IScreenSelectionService
{
    /// <inheritdoc />
    public Task<RemoteVisualAnchor?> PickVisualElementAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        return PickerSession.PickAsync(
            windowHelper,
            visualContext,
            initialMode,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<Bitmap?> TakeScreenshotAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        return ScreenshotSession.TakeAsync(
            windowHelper,
            visualContext,
            initialMode,
            cancellationToken);
    }
}