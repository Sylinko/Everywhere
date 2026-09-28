using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Provides the Windows interactive element and screenshot selection experience.
/// </summary>
public sealed partial class WindowsScreenSelectionService(IWindowHelper windowHelper) : IScreenSelectionService
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