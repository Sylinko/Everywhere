using Avalonia.Input;
using Avalonia.Input.TextInput;
using Everywhere.Patches.Contracts.Interop;

namespace Everywhere.Patches.Avalonia.Win32;

internal partial class patch_WindowImpl : IWindowInputFeature
{
    // A zero-initialized field preserves the original policy without modifying native constructors.
    public bool CanTakeFocusOnClick
    {
        get => !_everywherePreventMouseFocus;
        set => _everywherePreventMouseFocus = !value;
    }

    public ITextInputMethodImpl? TextInputMethod { get; set; }

    public IInputRoot? InputRoot => _owner;

    private bool _everywherePreventMouseFocus;

    protected virtual bool ShouldTakeFocusOnClick => !_everywherePreventMouseFocus;

    private extern object? orig_TryGetFeature(Type featureType);

    public object? TryGetFeature(Type featureType)
    {
        if (featureType == typeof(ITextInputMethodImpl) && TextInputMethod is { } method)
        {
            return method;
        }

        return orig_TryGetFeature(featureType);
    }
}