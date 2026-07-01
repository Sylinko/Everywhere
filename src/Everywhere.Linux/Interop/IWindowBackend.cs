using Avalonia;
using Avalonia.Controls;
using Everywhere.Interop;

namespace Everywhere.Linux.Interop;

/// <summary>
/// Abstract display backend contract (X11[Xorg] / Wayland Compositor / other).
/// </summary>
public interface IWindowBackend : IWindowHelper, IObservable<TextSelectionData>
{
    void SendKeyboardShortcut(KeyboardShortcut shortcut);

    PixelPoint GetPointer();

    IVisualElement? GetFocusedWindowElement();

    IVisualElement GetWindowElementAt(PixelPoint point);

    IVisualElement? GetWindowElementByInfo(int pid, PixelRect rect);

    IVisualElement GetScreenElement();

    IEnumerable<IVisualElement> Screens { get; }

    /// <summary>
    /// Capture screen bitmap of window within rect
    /// </summary>
    /// <param name="window">capture target, full screen(root window used) if not given</param>
    /// <param name="rect">captured rect relative to window</param>
    /// <returns></returns>
    IVisualElement.ICapturedBitmapData Capture(IVisualElement? window, PixelRect rect);

    void SetPickerWindow(Window? window);
}
