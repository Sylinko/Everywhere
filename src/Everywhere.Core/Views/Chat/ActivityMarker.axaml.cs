using Avalonia.Controls;

namespace Everywhere.Views;

public sealed class ActivityMarker : Expander
{
    public static readonly StyledProperty<object?> IconProperty =
        AvaloniaProperty.Register<ActivityMarker, object?>(nameof(Icon));

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly StyledProperty<object?> SecondaryHeaderProperty =
        AvaloniaProperty.Register<ActivityMarker, object?>(nameof(SecondaryHeader));

    public object? SecondaryHeader
    {
        get => GetValue(SecondaryHeaderProperty);
        set => SetValue(SecondaryHeaderProperty, value);
    }

    public static readonly StyledProperty<bool> IsSecondaryHeaderVisibleProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsSecondaryHeaderVisible), true);

    public bool IsSecondaryHeaderVisible
    {
        get => GetValue(IsSecondaryHeaderVisibleProperty);
        set => SetValue(IsSecondaryHeaderVisibleProperty, value);
    }

    public static readonly StyledProperty<bool> IsRunningProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsRunning));

    public bool IsRunning
    {
        get => GetValue(IsRunningProperty);
        set => SetValue(IsRunningProperty, value);
    }

    public static readonly StyledProperty<bool> IsExpandableProperty =
        AvaloniaProperty.Register<ActivityMarker, bool>(nameof(IsExpandable), true);

    public bool IsExpandable
    {
        get => GetValue(IsExpandableProperty);
        set => SetValue(IsExpandableProperty, value);
    }

    public static readonly StyledProperty<double> HorizontalSpacingProperty =
        AvaloniaProperty.Register<ActivityMarker, double>(nameof(HorizontalSpacing));

    public double HorizontalSpacing
    {
        get => GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public static readonly StyledProperty<double> VerticalSpacingProperty =
        AvaloniaProperty.Register<ActivityMarker, double>(nameof(VerticalSpacing));

    public double VerticalSpacing
    {
        get => GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }
}