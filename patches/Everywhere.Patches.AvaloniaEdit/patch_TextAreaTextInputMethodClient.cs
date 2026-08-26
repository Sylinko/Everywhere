// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global
// ReSharper disable UnusedType.Local
// ReSharper disable UnusedMember.Local

using Avalonia;
using Avalonia.Input.TextInput;
using AvaloniaEdit.Editing;
using Everywhere.Patches.Contracts.Views;
using MonoMod;

namespace Everywhere.Patches.AvaloniaEdit;

[MonoModPatch("AvaloniaEdit.Editing.TextArea")]
internal class patch_TextArea
{
    [MonoModPatch("TextAreaTextInputMethodClient")]
    private class patch_TextAreaTextInputMethodClient : TextInputMethodClient
    {
        [MonoModIgnore]
        private TextArea? _textArea;

        [MonoModIgnore]
        public extern override string SurroundingText { get; }

        [MonoModIgnore]
        public extern override Rect CursorRectangle { get; }

        [MonoModIgnore]
        public extern override TextSelection Selection { get; set; }

        [MonoModIgnore]
        public extern override Visual TextViewVisual { get; }

        [MonoModReplace]
        public override bool SupportsPreedit => true;

        [MonoModIgnore]
        public extern override bool SupportsSurroundingText { get; }

        [MonoModReplace]
        public override void SetPreeditText(string? text)
        {
            if (_textArea is not { } textArea) return;

            // Avalonia calls SetPreeditText(null) when an IME composition starts.
            // At this point the HWND has real keyboard focus and its HIMC is available,
            // so retry the cursor rectangle that may have been dropped during activation.
            if (text is null)
            {
                RaiseCursorRectangleChanged();
            }

            textArea.RaiseEvent(new PreeditChangedEventArgs(PreeditChangedEventRegistry.PreeditChangedEvent, text, CursorRectangle));
        }
    }
}