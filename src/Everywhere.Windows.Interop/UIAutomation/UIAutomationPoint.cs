namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Represents one UI Automation point in physical screen coordinates.
/// </summary>
/// <param name="X">The horizontal coordinate.</param>
/// <param name="Y">The vertical coordinate.</param>
public readonly record struct UIAutomationPoint(int X, int Y);