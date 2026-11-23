using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Everywhere.Interactions;

namespace Everywhere.Views;

/// <summary>
/// Hosts the typed onboarding decision templates for each <see cref="ViewModels.OobeStep"/>
/// with directional content transitions.
/// </summary>
public sealed partial class WelcomeDecisionView : TransitioningContentControl
{
    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    /// <summary>
    /// Initializes a new instance of the <see cref="WelcomeDecisionView"/> class.
    /// </summary>
    public WelcomeDecisionView()
    {
        InitializeComponent();
    }

    internal void SetSlideSuppressed(bool isSuppressed)
    {
        if (PageTransition is OobeSlideFadeTransition transition)
        {
            transition.IsSlideEnabled = !isSuppressed;
        }
    }

    private void HandleIntroTilePointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Visual tile && tile.FindDescendantOfType<IOobePreview>() is { } preview)
        {
            preview.Play();
        }
    }
}
