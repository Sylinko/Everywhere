using Avalonia.Controls.Primitives;
using Everywhere.Configuration;
using TextSelectionToolbarAction = Everywhere.TextSelection.TextSelectionToolbarAction;

namespace Everywhere.Views;

/// <summary>
/// Edits an isolated, same-type draft so cancelling cannot change the live toolbar or its icon.
/// </summary>
public sealed class TextSelectionToolbarActionForm(TextSelectionToolbarAction action) : TemplatedControl
{
    public static readonly DirectProperty<TextSelectionToolbarActionForm, TextSelectionToolbarAction> EditorProperty =
        AvaloniaProperty.RegisterDirect<TextSelectionToolbarActionForm, TextSelectionToolbarAction>(nameof(Editor), form => form.Editor);

    public TextSelectionToolbarAction Editor { get; } = action.CreateDraft();
}