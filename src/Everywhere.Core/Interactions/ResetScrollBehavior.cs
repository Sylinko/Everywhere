using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace Everywhere.Interactions;

/// <summary>
/// Resets scroll position back to the origin when <see cref="ContentToken"/> changes to a new step
/// without disturbing interactive scrolling during edits inside the current step.
/// </summary>
public sealed class ResetScrollBehavior : Behavior<ScrollViewer>
{
    /// <summary>
    /// Defines the <see cref="ContentToken"/> property.
    /// </summary>
    public static readonly StyledProperty<object?> ContentTokenProperty =
        AvaloniaProperty.Register<ResetScrollBehavior, object?>(nameof(ContentToken));

    public object? ContentToken
    {
        get => GetValue(ContentTokenProperty);
        set => SetValue(ContentTokenProperty, value);
    }

    /// <inheritdoc />
    protected override void OnAttached()
    {
        base.OnAttached();
        ResetScroll();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentTokenProperty || change.Property == IsEnabledProperty)
        {
            ResetScroll();
        }
    }

    private void ResetScroll()
    {
        if (IsEnabled && AssociatedObject is { } scrollViewer)
        {
            scrollViewer.Offset = default;
        }
    }
}
