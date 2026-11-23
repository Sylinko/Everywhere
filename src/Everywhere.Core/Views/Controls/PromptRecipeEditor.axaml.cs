using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Shared guided-prompt card combining scenarios, tone, segmented detail depth, and an inline
/// preferences disclosure. When <see cref="IsPersonalization"/> is false in Prompt Management,
/// the prompt name field appears at the top of the optional preferences disclosure.
/// </summary>
public partial class PromptRecipeEditor : UserControl
{
    private static readonly Dictionary<string, string> OptionAccentClasses = new(StringComparer.Ordinal)
    {
        ["general-qa"] = "accent-cyan",
        ["programming-development"] = "accent-blue",
        ["code-review"] = "accent-purple",
        ["writing-editing"] = "accent-pink",
        ["data-analysis"] = "accent-orange",
        ["learning-education"] = "accent-green",
        ["translation-language"] = "accent-teal",
        ["product-planning"] = "accent-gold",
        ["troubleshooting"] = "accent-red",
        ["research-planning"] = "accent-indigo",

        ["professional-rigorous"] = "accent-blue",
        ["friendly-patient"] = "accent-pink",
        ["concise-direct"] = "accent-cyan",
        ["socratic-guiding"] = "accent-gold",
        ["formal-business"] = "accent-indigo",
        ["creative-exploratory"] = "accent-purple",

        ["concise"] = "accent-green",
        ["balanced"] = "accent-blue",
        ["detailed"] = "accent-purple",

        ["general"] = "accent-pink",
        ["professional"] = "accent-blue",
        ["programming"] = "accent-cyan",
        ["writing"] = "accent-orange",
        ["translation"] = "accent-teal",
        ["research"] = "accent-purple",
        ["learning"] = "accent-green",
        ["product"] = "accent-gold",

        ["automatic"] = "accent-purple",
        ["conclusion-first"] = "accent-gold",
        ["step-by-step"] = "accent-blue",
        ["key-points"] = "accent-green",
        ["compare-options"] = "accent-orange",
        ["examples-first"] = "accent-pink",
        ["natural-conversation"] = "accent-cyan"
    };

    /// <summary>
    /// Defines the <see cref="Editor"/> property.
    /// </summary>
    public static readonly StyledProperty<PromptEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<PromptRecipeEditor, PromptEditorViewModel?>(nameof(Editor));

    /// <summary>
    /// Defines the <see cref="IsPersonalization"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsPersonalizationProperty =
        AvaloniaProperty.Register<PromptRecipeEditor, bool>(nameof(IsPersonalization));

    /// <summary>
    /// Defines the <see cref="ShowScenarioRequiredHint"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> ShowScenarioRequiredHintProperty =
        AvaloniaProperty.Register<PromptRecipeEditor, bool>(nameof(ShowScenarioRequiredHint));

    /// <summary>
    /// Defines the attached <c>OptionAccentId</c> property used to assign semantic color classes to option chips.
    /// </summary>
    public static readonly AttachedProperty<string?> OptionAccentIdProperty =
        AvaloniaProperty.RegisterAttached<PromptRecipeEditor, Control, string?>("OptionAccentId");

    static PromptRecipeEditor()
    {
        OptionAccentIdProperty.Changed.AddClassHandler<Control>(HandleOptionAccentIdChanged);
    }

    public PromptEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public bool IsPersonalization
    {
        get => GetValue(IsPersonalizationProperty);
        set => SetValue(IsPersonalizationProperty, value);
    }

    public bool ShowScenarioRequiredHint
    {
        get => GetValue(ShowScenarioRequiredHintProperty);
        set => SetValue(ShowScenarioRequiredHintProperty, value);
    }

    public PromptRecipeEditor()
    {
        InitializeComponent();
        OptionalPreferencesPanel.TargetContent = OptionalPreferencesPresenter.Content;
    }

    public static string? GetOptionAccentId(Control element) => element.GetValue(OptionAccentIdProperty);

    public static void SetOptionAccentId(Control element, string? value) => element.SetValue(OptionAccentIdProperty, value);

    private static void HandleOptionAccentIdChanged(Control control, AvaloniaPropertyChangedEventArgs args)
    {
        for (var i = control.Classes.Count - 1; i >= 0; i--)
        {
            if (control.Classes[i].StartsWith("accent-", StringComparison.Ordinal))
            {
                control.Classes.RemoveAt(i);
            }
        }

        if (args.NewValue is string id && OptionAccentClasses.TryGetValue(id, out var accentClass))
        {
            control.Classes.Add(accentClass);
        }
    }
}
