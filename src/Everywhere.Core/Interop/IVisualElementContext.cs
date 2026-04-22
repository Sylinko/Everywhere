using Avalonia.Media.Imaging;

namespace Everywhere.Interop;

/// <summary>
/// Represents the mode of screen selection.
/// Used when picking elements or taking screenshots.
/// </summary>
[Flags]
public enum ScreenSelectionModes
{
    /// <summary>
    /// Pick a whole screen.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Screen)]
    Screen = 0x1,

    /// <summary>
    /// Pick a window.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Window)]
    Window = 0x2,

    /// <summary>
    /// Pick a specific element.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Element)]
    Element = 0x4,

    /// <summary>
    /// Free selection mode.
    /// </summary>
    [DynamicResourceKey(LocaleKey.ScreenSelectionMode_Free)]
    Free = 0x8,

    All = Screen | Window | Element | Free
}

/// <summary>
/// Represents data about text selection.
/// Used in IVisualElementContext to notify about text selection changes.
/// </summary>
/// <param name="Text">The selected text, or null if no text is selected.</param>
/// <param name="Element">The visual element from which the text is selected, or null if no element is associated.</param>
public readonly record struct TextSelectionData(
    string? Text,
    IVisualElement? Element
);

/// <summary>
/// Represents a context for visual elements, providing methods to interact with them.
/// </summary>
/// <remarks>
/// This interface extends IObservable to allow observers to subscribe to text selection changes.
/// Warning: Implementers should ensure that related hooks only exist when there are active subscribers
/// to avoid unnecessary resource usage and side effects (e.g. unnecessary clipboard monitoring).
/// </remarks>
public interface IVisualElementContext : IObservable<TextSelectionData>
{
    /// <summary>
    /// Get the currently focused element.
    /// </summary>
    IVisualElement? FocusedElement { get; }

    /// <summary>
    /// Get all screens available in the system.
    /// </summary>
    IEnumerable<IVisualElement> Screens { get; }

    /// <summary>
    /// Get the element at the specified point.
    /// </summary>
    /// <param name="point">Point in screen pixels.</param>
    /// <param name="modes"></param>
    /// <returns></returns>
    IVisualElement? ElementFromPoint(PixelPoint point, ScreenSelectionModes modes = ScreenSelectionModes.Element);

    /// <summary>
    /// Get the element under the mouse pointer.
    /// </summary>
    /// <param name="modes"></param>
    /// <returns></returns>
    IVisualElement? ElementFromPointer(ScreenSelectionModes modes = ScreenSelectionModes.Element);

    /// <summary>
    /// Get the element from a native window handle.
    /// </summary>
    /// <param name="windowHandle"></param>
    /// <returns></returns>
    IVisualElement? ElementFromWindowHandle(nint windowHandle);

    /// <summary>
    /// Let the user pick an element from the screen.
    /// </summary>
    /// <param name="initialMode">
    /// The initial pick mode to use. If null, it remembers the last used mode.
    /// </param>
    /// <returns></returns>
    Task<IVisualElement?> PickVisualElementAsync(ScreenSelectionModes? initialMode);

    /// <summary>
    /// Let the user take a screenshot of a selected area.
    /// </summary>
    /// <param name="initialMode">
    /// The initial pick mode to use. If null, it remembers the last used mode.
    /// </param>
    /// <returns></returns>
    Task<Bitmap?> TakeScreenshotAsync(ScreenSelectionModes? initialMode);

    /// <summary>
    /// Let the user pick multiple elements from the screen.
    /// The user can left-click to select/deselect visual elements, right-click or esc to finish.
    /// </summary>
    /// <param name="initialMode"></param>
    /// <returns></returns>
    Task<IReadOnlyList<IVisualElement>> SelectMultipleVisualElementsAsync(ScreenSelectionModes? initialMode);
}