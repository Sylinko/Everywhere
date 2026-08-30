namespace Everywhere.Windows.Interop.UIAutomation;

/// <summary>
/// Represents one UI Automation bounding rectangle in physical screen coordinates.
/// </summary>
/// <param name="Left">The left coordinate.</param>
/// <param name="Top">The top coordinate.</param>
/// <param name="Right">The right coordinate.</param>
/// <param name="Bottom">The bottom coordinate.</param>
public readonly record struct UIAutomationRectangle(int Left, int Top, int Right, int Bottom);