using Everywhere.Automation;
using Everywhere.Interop;
using Bitmap = Avalonia.Media.Imaging.Bitmap;

namespace Everywhere.Windows.Interop;

/// <summary>
/// Provides the Windows interactive element and screenshot selection experience.
/// </summary>
public sealed partial class WindowsScreenSelectionService(IWindowHelper windowHelper, IVisualElementBackend visualElementBackend)
    : IScreenSelectionService
{
    private readonly VisualContext _transientContext = new();

    /// <inheritdoc />
    public Task<VisualElementQueryResult?> PickVisualElementAsync(VisualElementRetention retention, ScreenSelectionMode? initialMode)
    {
        return PickerSession.PickAsync(windowHelper, visualElementBackend, retention, initialMode);
    }

    /// <inheritdoc />
    public Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode)
    {
        return ScreenshotSession.TakeAsync(windowHelper, visualElementBackend, _transientContext, initialMode);
    }
}