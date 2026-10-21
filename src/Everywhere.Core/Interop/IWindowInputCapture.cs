namespace Everywhere.Interop;

/// <summary>
/// Owns non-activating input capture. All members, events and disposal belong to the UI thread.
/// </summary>
public interface IWindowInputCapture : IDisposable
{
    /// <summary>
    /// Whether this capture still owns the native input route.
    /// </summary>
    bool IsActive { get; }

    /// <summary>
    /// Whether the input method is composing text rather than delivering application shortcuts.
    /// </summary>
    bool IsComposing { get; }

    /// <summary>
    /// Raised after involuntary capture loss. Explicit disposal does not raise this event.
    /// </summary>
    event EventHandler? Lost;
}