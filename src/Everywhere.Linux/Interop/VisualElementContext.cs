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

    public IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        try
        {
            switch (mode)
            {
                case ScreenSelectionMode.Element:
                    var win = backend.GetWindowElementAt(point);
                    var elem = _atspi.ElementFromWindow(point, win);
                    return elem ?? win; // fallback to window mode

                case ScreenSelectionMode.Window:
                    return backend.GetWindowElementAt(point);

                case ScreenSelectionMode.Screen:
                    return backend.GetScreenElement();

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ElementFromPoint failed for point {point}, mode {mode}", point, mode);
            return null;
        }
    }

    public IVisualElement? ElementFromPointer(ScreenSelectionMode mode = ScreenSelectionMode.Element)
    {
        var point = backend.GetPointer();
        return ElementFromPoint(point, mode);
    }

    public IVisualElement? ElementFromWindowHandle(IntPtr windowHandle)
    {
        throw new NotImplementedException();
    }

    public async Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionMode? initialMode)
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

    public async Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionMode? initialMode)
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