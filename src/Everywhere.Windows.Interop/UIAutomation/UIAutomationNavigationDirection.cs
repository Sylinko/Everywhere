namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Identifies one lazy UI Automation TreeWalker navigation operation.
/// </summary>
public enum UIAutomationNavigationDirection
{
    /// <summary>
    /// Navigates to the parent.
    /// </summary>
    Parent,

    /// <summary>
    /// Navigates to the first child.
    /// </summary>
    FirstChild,

    /// <summary>
    /// Navigates to the previous sibling.
    /// </summary>
    PreviousSibling,

    /// <summary>
    /// Navigates to the next sibling.
    /// </summary>
    NextSibling,
}