using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Provides the Windows interactive element and screenshot selection experience.
/// </summary>
public sealed partial class WindowsScreenSelectionService(IWindowHelper windowHelper, IVisualElementBackend visualElementBackend)
    : IScreenSelectionService, IDisposable
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
    public Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode)
    {
        return ScreenshotSession.TakeAsync(windowHelper, visualElementBackend, _transientContext, initialMode);
    }

    /// <summary>Releases the transient Context used only during interactive selection.</summary>
    public void Dispose() => _transientContext.Dispose();
}