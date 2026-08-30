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
    /// Requests the Value property used for common scalar text.
    /// </summary>
    Value = 1 << 12,

    /// <summary>
    /// Requests the TextPattern used for document-style ranged text reads.
    /// </summary>
    TextPattern = 1 << 13,

    /// <summary>
    /// Requests the InvokePattern used for a control's primary action.
    /// </summary>
    InvokePattern = 1 << 14,

    /// <summary>
    /// Requests the TogglePattern used for state-cycling controls.
    /// </summary>
    TogglePattern = 1 << 15,

    /// <summary>
    /// Requests the SelectionItemPattern used for selectable items.
    /// </summary>
    SelectionItemPattern = 1 << 16,

    /// <summary>
    /// Requests the ExpandCollapsePattern used for expandable controls.
    /// </summary>
    ExpandCollapsePattern = 1 << 17,

    /// <summary>
    /// Requests the SelectionPattern used by containers that expose selected child elements.
    /// </summary>
    SelectionPattern = 1 << 18,

    /// <summary>
    /// Requests the LegacyIAccessiblePattern used as a compatibility fallback for MSAA-backed controls.
    /// </summary>
    LegacyIAccessiblePattern = 1 << 19,

    /// <summary>
    /// Requests the ValuePattern used for scalar-value mutation and access.
    /// </summary>
    ValuePattern = 1 << 20,

    /// <summary>
    /// Requests the current expand-or-collapse state.
    /// </summary>
    ExpandCollapseState = 1 << 21,
}