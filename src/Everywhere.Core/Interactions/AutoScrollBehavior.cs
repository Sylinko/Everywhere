using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;

namespace Everywhere.Interactions;

public enum AutoScrollBehaviorMode
{
    None,
    Always,
    WhenAtEnd
}

public class AutoScrollBehavior : Behavior<ScrollViewer>
{
    public static readonly StyledProperty<AutoScrollBehaviorMode> ModeProperty =
        AvaloniaProperty.Register<AutoScrollBehavior, AutoScrollBehaviorMode>(nameof(Mode), AutoScrollBehaviorMode.WhenAtEnd);

    public AutoScrollBehaviorMode Mode
    {
        get => GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    private bool _isAtEnd = true;

    protected override void OnAttached()
    {
        base.OnAttached();

        if (AssociatedObject is not null)
        {
            AssociatedObject.PropertyChanged += OnScrollViewerPropertyChanged;
        }
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (AssociatedObject is not { } scrollViewer) return;

        if (e.Property != ScrollViewer.OffsetProperty &&
            e.Property != ScrollViewer.ViewportProperty &&
            e.Property != ScrollViewer.ExtentProperty) return;

        if (e.Property == ScrollViewer.OffsetProperty)
        {
            _isAtEnd = e.NewValue.To<Vector>().Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height;
        }

        if (Mode == AutoScrollBehaviorMode.Always || Mode == AutoScrollBehaviorMode.WhenAtEnd && _isAtEnd)
        {
            scrollViewer.Offset = new Vector(scrollViewer.Offset.X, double.PositiveInfinity);
        }
    }
}