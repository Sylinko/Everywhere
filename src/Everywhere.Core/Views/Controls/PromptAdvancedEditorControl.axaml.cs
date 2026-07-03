using Avalonia.Controls.Primitives;
using Avalonia.Data;

namespace Everywhere.Views;

/// <summary>
/// Reusable prompt name, template editor, and placeholder reference block for advanced editing.
/// </summary>
/// <remarks>
/// Prompt creation and existing-prompt editing share this control so advanced-mode affordances stay
/// consistent while each page keeps its own save/navigation lifecycle.
/// </remarks>
public sealed class PromptAdvancedEditorControl : TemplatedControl
{
    public static readonly StyledProperty<string?> PromptNameProperty =
        AvaloniaProperty.Register<PromptAdvancedEditorControl, string?>(
            nameof(PromptName),
            defaultBindingMode: BindingMode.TwoWay);
    public string? PromptName
    {
        get => GetValue(PromptNameProperty);
        set => SetValue(PromptNameProperty, value);
    }

    public static readonly StyledProperty<string?> PromptTextProperty =
        AvaloniaProperty.Register<PromptAdvancedEditorControl, string?>(
            nameof(PromptText),
            defaultBindingMode: BindingMode.TwoWay);

    public string? PromptText
    {
        get => GetValue(PromptTextProperty);
        set => SetValue(PromptTextProperty, value);
    }

    public static readonly StyledProperty<IReadOnlyList<PromptPlaceholderReferenceItem>?> PlaceholderReferencesProperty =
        AvaloniaProperty.Register<PromptAdvancedEditorControl, IReadOnlyList<PromptPlaceholderReferenceItem>?>(nameof(PlaceholderReferences));

    public IReadOnlyList<PromptPlaceholderReferenceItem>? PlaceholderReferences
    {
        get => GetValue(PlaceholderReferencesProperty);
        set => SetValue(PlaceholderReferencesProperty, value);
    }
}