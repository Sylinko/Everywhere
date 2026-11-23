using Avalonia.Controls;

namespace Everywhere.Views;

/// <summary>
/// Centered frosted glass shortcut confirmation card for onboarding, presenting the
/// global toggle, tactile keycap recorder stage, and optional live try-it trigger.
/// </summary>
public sealed partial class OobeShortcutCard : UserControl
{
    /// <summary>
    /// Defines the <see cref="Flow"/> property.
    /// </summary>
    public static readonly StyledProperty<WelcomeFlow?> FlowProperty =
        AvaloniaProperty.Register<OobeShortcutCard, WelcomeFlow?>(nameof(Flow));

    /// <summary>
    /// Gets or sets the onboarding flow driving the shortcut card.
    /// </summary>
    public WelcomeFlow? Flow
    {
        get => GetValue(FlowProperty);
        set => SetValue(FlowProperty, value);
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="OobeShortcutCard"/> class.
    /// </summary>
    public OobeShortcutCard()
    {
        InitializeComponent();
    }
}
