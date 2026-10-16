using Avalonia.Media.Imaging;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides the macOS interactive element and screenshot selection experience.
/// </summary>
public sealed partial class MacScreenSelectionService(IWindowHelper windowHelper, INativeHelper nativeHelper) : IScreenSelectionService
{
    /// <inheritdoc />
    public Task<RemoteVisualAnchor?> PickApplicationWindowAsync(
        IHostedVisualContext visualContext,
        CancellationToken cancellationToken = default)
    {
        return PickerSession.PickAsync(
            windowHelper,
            nativeHelper,
            visualContext,
            ScreenSelectionMode.Window,
            isApplicationWindowPicker: true,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<RemoteVisualAnchor?> PickVisualElementAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default)
    {
        return PickerSession.PickAsync(
            windowHelper,
            nativeHelper,
            visualContext,
            initialMode,
            isApplicationWindowPicker: false,
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
            nativeHelper,
            visualContext,
            initialMode,
            cancellationToken);
    }
}