using Avalonia.Controls.Primitives;

namespace Everywhere.Views;

public sealed class QuickModeVisualElementCardContainer : TemplatedControl
{
    public static readonly StyledProperty<double> RotateAngleProperty =
        AvaloniaProperty.Register<QuickModeVisualElementCardContainer, double>(nameof(RotateAngle));

    public double RotateAngle
    {
        get => GetValue(RotateAngleProperty);
        set => SetValue(RotateAngleProperty, value);
    }
}