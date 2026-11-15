namespace Everywhere.Interop;

/// <summary>
/// Separates native window activation from logical editor focus and delegated input.
/// </summary>
public enum WindowInputMode
{
    /// <summary>
    /// Ordinary interaction may activate the window and give it keyboard focus.
    /// </summary>
    Normal,

    /// <summary>
    /// Pointer interaction does not activate the window or acquire keyboard focus.
    /// </summary>
    NonActivating,

    /// <summary>
    /// Enables logical editor focus and the non-activating input adapter. Does not prepare or capture input.
    /// </summary>
    NonActivatingTextInput
}