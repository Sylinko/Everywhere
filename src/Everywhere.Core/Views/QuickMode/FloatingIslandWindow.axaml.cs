using Avalonia.Input;
using Avalonia.Interactivity;

namespace Everywhere.Views;

public sealed partial class FloatingIslandWindow : ReactiveWindow<FloatingIslandWindowViewModel>
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<FloatingIslandWindow, bool>(nameof(IsExpanded));

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public static readonly DirectProperty<FloatingIslandWindow, bool> IsPressingProperty =
        AvaloniaProperty.RegisterDirect<FloatingIslandWindow, bool>(
        nameof(IsPressing),
        o => o.IsPressing);

    public bool IsPressing
    {
        get;
        private set => SetAndRaise(IsPressingProperty, ref field, value);
    }

    public FloatingIslandWindow()
    {
        InitializeComponent();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        IsPressing = true;
        BeginMoveDrag(e);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);

        if (IsPressing && e.InitialPressMouseButton == MouseButton.Left)
        {
            IsExpanded = true;
            IsPressing = false;
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);

        IsExpanded = false;
    }
}