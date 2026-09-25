using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Everywhere.Common;

namespace Everywhere.Views;

public class IconPresenter : TemplatedControl
{
    public static readonly StyledProperty<ColoredIcon?> IconProperty =
        AvaloniaProperty.Register<IconPresenter, ColoredIcon?>(nameof(Icon));

    public ColoredIcon? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<IconPresenter, double>(nameof(Size), double.NaN);

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<IconPresenter, double>(nameof(IconSize));

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly StyledProperty<double> LineHeightProperty = TextBlock.LineHeightProperty.AddOwner<IconPresenter>();

    public double LineHeight
    {
        get => GetValue(LineHeightProperty);
        set => SetValue(LineHeightProperty, value);
    }

    public IconPresenter()
    {
        this[!WidthProperty] = this[!SizeProperty];
        this[!HeightProperty] = this[!SizeProperty];
    }
}