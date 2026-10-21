using Avalonia.Input;
using Avalonia.Input.TextInput;

namespace Everywhere.Patches.Contracts.Interop;

/// <summary>
/// Per-window native focus policy and input-method override, configured on the UI thread.
/// </summary>
public interface IWindowInputFeature
{
    /// <summary>
    /// Whether native mouse handling may call SetFocus for this window. Defaults to true.
    /// </summary>
    bool CanTakeFocusOnClick { get; set; }

    /// <summary>
    /// Overrides the platform input method. Null preserves the original platform implementation.
    /// </summary>
    ITextInputMethodImpl? TextInputMethod { get; set; }

    /// <summary>
    /// The platform input root used when delivering delegated input through Avalonia's normal pipeline.
    /// </summary>
    IInputRoot? InputRoot { get; }
}