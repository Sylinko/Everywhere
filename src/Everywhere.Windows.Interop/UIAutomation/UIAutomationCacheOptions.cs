namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Identifies the bounded UI Automation properties and patterns collected with an element reference.
/// </summary>
[Flags]
public enum UIAutomationCacheOptions
{
    /// <summary>
    /// Requests no cached data.
    /// </summary>
    None = 0,

    /// <summary>
    /// Requests the opaque UI Automation RuntimeId.
    /// </summary>
    RuntimeId = 1 << 0,

    /// <summary>
    /// Requests the UI Automation control type.
    /// </summary>
    ControlType = 1 << 1,

    /// <summary>
    /// Requests the bounding rectangle.
    /// </summary>
    BoundingRectangle = 1 << 2,

    /// <summary>
    /// Requests the provider process identifier.
    /// </summary>
    ProcessId = 1 << 3,

    /// <summary>
    /// Requests the native window handle.
    /// </summary>
    NativeWindowHandle = 1 << 4,

    /// <summary>
    /// Requests the off-screen state.
    /// </summary>
    IsOffscreen = 1 << 5,

    /// <summary>
    /// Requests the enabled state.
    /// </summary>
    IsEnabled = 1 << 6,

    /// <summary>
    /// Requests the keyboard-focus state.
    /// </summary>
    HasKeyboardFocus = 1 << 7,

    /// <summary>
    /// Requests the selection-item selected state.
    /// </summary>
    IsSelected = 1 << 8,

    /// <summary>
    /// Requests the value read-only state.
    /// </summary>
    IsReadOnly = 1 << 9,

    /// <summary>
    /// Requests the password state.
    /// </summary>
    IsPassword = 1 << 10,

    /// <summary>
    /// Requests the accessible name.
    /// </summary>
    Name = 1 << 11,

    /// <summary>
    /// Requests the TextPattern used for bounded text reads.
    /// </summary>
    Text = 1 << 12,
}