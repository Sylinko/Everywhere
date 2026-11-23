using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Onboarding host wrapper around <see cref="PromptRecipeEditor"/> bound to <see cref="WelcomeFlow.Personalization"/>.
/// </summary>
public sealed partial class OobePersonalizeCard : UserControl
{
    /// <summary>
    /// Defines the <see cref="Flow"/> property.
    /// </summary>
    public static readonly StyledProperty<WelcomeFlow?> FlowProperty =
        AvaloniaProperty.Register<OobePersonalizeCard, WelcomeFlow?>(nameof(Flow));

    /// <summary>
    /// Gets or sets the onboarding flow driving the personalization card.
    /// </summary>
    public WelcomeFlow? Flow
    {
        get => GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OobePersonalizeCard"/> class.
    /// </summary>
    public OobePersonalizeCard()
    {
        InitializeComponent();
    }
}
