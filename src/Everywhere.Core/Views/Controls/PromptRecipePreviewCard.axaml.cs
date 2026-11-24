using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Renders a compact example user/assistant exchange reflecting the current prompt recipe settings.
/// Shared by both OOBE personalization and the Prompt Editor quick-configuration preview.
/// </summary>
public sealed partial class PromptRecipePreviewCard : UserControl
{
    /// <summary>
    /// Defines the <see cref="QuestionKey"/> property.
    /// </summary>
    public static readonly StyledProperty<IDynamicLocaleKey?> QuestionKeyProperty =
        AvaloniaProperty.Register<PromptRecipePreviewCard, IDynamicLocaleKey?>(nameof(QuestionKey));

    /// <summary>
    /// Defines the <see cref="ReplyKey"/> property.
    /// </summary>
    public static readonly StyledProperty<IDynamicLocaleKey?> ReplyKeyProperty =
        AvaloniaProperty.Register<PromptRecipePreviewCard, IDynamicLocaleKey?>(nameof(ReplyKey));

    /// <summary>
    /// Gets or sets the localized key for the example user question bubble.
    /// </summary>
    public IDynamicLocaleKey? QuestionKey
    {
        get => GetValue(QuestionKeyProperty);
        set => SetValue(QuestionKeyProperty, value);
    }

    /// <summary>
    /// Gets or sets the localized key for the example assistant reply bubble.
    /// </summary>
    public IDynamicLocaleKey? ReplyKey
    {
        get => GetValue(ReplyKeyProperty);
        set => SetValue(ReplyKeyProperty, value);
    }

    public PromptRecipePreviewCard()
    {
        InitializeComponent();
    }
}
