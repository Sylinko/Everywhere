using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Controls.ApplicationLifetimes;
using Everywhere.Interop;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Linux IVisualElementContext Impl:
/// Using IWindowBackend for keyboard/mouse event and windows info.
/// Using AtspiService to access UI elements inside window.
/// </summary>
public partial class VisualElementContext(
    IWindowBackend backend,
    ILogger<VisualElementContext> logger
) : IVisualElementContext
{
    public IVisualElement? FocusedElement
    {
        get
        {
            try
            {
                // some app do not support atspi focus event
                // ensure the app is correct by pid
                // however, atspi element is more detailed, so return null when incorrect
                // to let atspi pointer search do the work
                var focused = _atspi.ElementFocused();
                var focusedWindow = backend.GetFocusedWindowElement();
                // for Non X11 session, the window may get null, and not equal to that atspi gives
                return (focusedWindow == null || (focused?.ProcessId == focusedWindow.ProcessId)) ? focused : null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AtspiFocusedElement failed");
                return null;
            }
        }
    }

    public IEnumerable<IVisualElement> Screens => backend.Screens;

    private readonly AtspiService _atspi = new(backend);

    public IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionModes modes = ScreenSelectionModes.Element)
    {
        try
        {
            switch (modes)
            {
                case ScreenSelectionModes.Element:
                    var win = backend.GetWindowElementAt(point);
                    var elem = _atspi.ElementFromWindow(point, win);
                    return elem ?? win; // fallback to window mode

                case ScreenSelectionModes.Window:
                    return backend.GetWindowElementAt(point);

                case ScreenSelectionModes.Screen:
                    return backend.GetScreenElement();

                default:
                    throw new ArgumentOutOfRangeException(nameof(modes), modes, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ElementFromPoint failed for point {point}, mode {mode}", point, modes);
            return null;
        }
    }

    public IVisualElement? ElementFromPointer(ScreenSelectionModes modes = ScreenSelectionModes.Element)
    {
        var point = backend.GetPointer();
        return ElementFromPoint(point, modes);
    }

    public IVisualElement? ElementFromWindowHandle(IntPtr windowHandle)
    {
        throw new NotImplementedException();
    }

    public async Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionModes? initialMode)
    {
        if (Application.Current is not { ApplicationLifetime: ClassicDesktopStyleApplicationLifetime desktopLifetime })
        {
            return null;
        }

        var windows = desktopLifetime.Windows.AsValueEnumerable().Where(w => w.IsVisible).ToList();
        foreach (var window in windows) window.Hide();
        var result = await ElementPicker.PickAsync(this, backend, initialMode);
        foreach (var window in windows) window.IsVisible = true;
        return result;
    }

    public async Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionModes? initialMode)
    {
        if (Application.Current is not { ApplicationLifetime: ClassicDesktopStyleApplicationLifetime desktopLifetime })
        {
            return null;
        }

        var windows = desktopLifetime.Windows.AsValueEnumerable().Where(w => w.IsVisible).ToList();
        foreach (var window in windows) window.Hide();

        var result = await ScreenshotPicker.TakeAsync(this, backend, initialMode);

        foreach (var window in windows) window.IsVisible = true;
        return result;
    }

    public IDisposable Subscribe(IObserver<TextSelectionData> observer)
    {
        return backend.Subscribe(observer);
    }
}