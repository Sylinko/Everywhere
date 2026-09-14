namespace Everywhere.Automation;

/// <summary>
/// Represents a recoverable failure reported by a native visual-element provider.
/// </summary>
public sealed class VisualElementProviderException : InvalidOperationException
{
    /// <summary>
    /// Gets the platform-independent classification of the provider failure.
    /// </summary>
    public VisualElementQueryFailureKind Kind { get; }

    /// <summary>
    /// Initializes a recoverable native-provider failure.
    /// </summary>
    public VisualElementProviderException(
        VisualElementQueryFailureKind kind,
        string message,
        Exception? innerException = null
    ) : base(message, innerException)
    {
        if (kind is not (VisualElementQueryFailureKind.ElementUnavailable or VisualElementQueryFailureKind.ProviderFailure))
        {
            throw new ArgumentOutOfRangeException(
                nameof(kind),
                kind,
                "Use the standard timeout or unsupported exception type for this failure kind.");
        }

        Kind = kind;
    }
}