namespace Everywhere.Interop;

/// <summary>
/// Represents the mode of an interactive screen selection.
/// </summary>
public enum ScreenSelectionMode
{
    /// <summary>
    /// Select a whole screen.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ScreenSelectionMode_Screen)]
    Screen,

    /// <summary>
    /// Select a window.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ScreenSelectionMode_Window)]
    Window,

    /// <summary>
    /// Select a specific accessibility element.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ScreenSelectionMode_Element)]
    Element,

    /// <summary>
    /// Select an arbitrary rectangular region.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ScreenSelectionMode_Free)]
    Free,
}