using Everywhere.Automation;

namespace Everywhere.Interop;

/// <summary>
/// Represents an observed text selection and the best-effort locator of its source element.
/// </summary>
/// <param name="Text">The selected text, or <see langword="null" /> when no text is selected.</param>
/// <param name="Locator">The best-effort locator for reacquiring the source element in the destination chat context.</param>
/// <param name="Resolution">The topological result resolved from <paramref name="Locator" />.</param>
public readonly record struct TextSelectionData(
    string? Text,
    VisualElementLocator? Locator,
    VisualElementResolution Resolution = VisualElementResolution.Direct
);