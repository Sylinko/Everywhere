namespace Everywhere.Mac.Interop;

/// <summary>
/// Defines common accessibility attribute and action constants.
/// </summary>
public static class AXAttributeConstants
{
    public static readonly NSString Role = new("AXRole");
    public static readonly NSString Subrole = new("AXSubrole");
    public static readonly NSString Parent = new("AXParent");
    public static readonly NSString Children = new("AXChildren");
    public static readonly NSString VisibleChildren = new("AXVisibleChildren");
    public static readonly NSString Title = new("AXTitle");
    public static readonly NSString Description = new("AXDescription");
    public static readonly NSString Value = new("AXValue");
    public static readonly NSString Position = new("AXPosition");
    public static readonly NSString Size = new("AXSize");
    public static readonly NSString Enabled = new("AXEnabled");
    public static readonly NSString Focused = new("AXFocused");
    public static readonly NSString Window = new("AXWindow");
    public static readonly NSString Windows = new("AXWindows");
    public static readonly NSString TopLevelUIElement = new("AXTopLevelUIElement");
    public static readonly NSString FocusedUIElement = new("AXFocusedUIElement");
    public static readonly NSString SelectedText = new("AXSelectedText");
    public static readonly NSString Selected = new("AXSelected");
    public static readonly NSString Hidden = new("AXHidden");
    public static readonly NSString FocusedWindow = new("AXFocusedWindow");
    public static readonly NSString NumberOfCharacters = new("AXNumberOfCharacters");
    public static readonly NSString StringForRange = new("AXStringForRange");
    public static readonly NSString IndexForChildUIElement = new("AXIndexForChildUIElement");

    // Additional attributes can be added here as needed
    public static readonly NSString EnhancedUserInterface = new("AXEnhancedUserInterface");
    public static readonly NSString ManualAccessibility = new("AXManualAccessibility");

    // Actions
    public static readonly NSString Press = new("AXPress");
}