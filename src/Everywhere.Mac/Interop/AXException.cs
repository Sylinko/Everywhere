namespace Everywhere.Mac.Interop;

/// <summary>
/// Preserves one native macOS Accessibility error at the managed platform boundary.
/// </summary>
public sealed class AXException(AXError error, string message) : Exception(message)
{
    public AXError Error { get; } = error;
}