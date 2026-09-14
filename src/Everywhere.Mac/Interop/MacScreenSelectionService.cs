using Avalonia.Media.Imaging;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.Mac.Automation;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Mac.Interop;

/// <summary>
/// Provides the macOS interactive element and screenshot selection experience.
/// </summary>
public sealed partial class MacScreenSelectionService(
    IWindowHelper windowHelper,
    MacVisualElementBackend visualElementBackend
) : IScreenSelectionService, IDisposable
{
    private readonly VisualContext _transientContext = new();

    /// <inheritdoc />
    public Task<RemoteVisualAnchor?> PickVisualElementAsync(
        IHostedVisualContext visualContext,
        ScreenSelectionMode? initialMode,
        CancellationToken cancellationToken = default) =>
        PickerSession.PickAsync(
            windowHelper,
            visualElementBackend,
            _transientContext,
            visualContext,
            initialMode,
            cancellationToken);

    /// <inheritdoc />
    public Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode) =>
        ScreenshotSession.TakeAsync(windowHelper, visualElementBackend, _transientContext, initialMode);

    /// <summary>
    /// Releases the transient Context used only by screenshot selection.
    /// </summary>
    public void Dispose() => _transientContext.Dispose();
}