using System.Globalization;
using Avalonia.Controls.Primitives;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Everywhere.Cloud;
using Everywhere.Common;

namespace Everywhere.Views;

public class UserProfilePresenter : TemplatedControl
{
    public static ICloudClient CloudClient { get; } = ServiceLocator.Resolve<ICloudClient>();

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<UserProfilePresenter, double>(nameof(Size), 16d);

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowActionsButtonProperty =
        UserProfileCard.ShowActionsButtonProperty.AddOwner<UserProfilePresenter>();

    public bool ShowActionsButton
    {
        get => GetValue(ShowActionsButtonProperty);
        set => SetValue(ShowActionsButtonProperty, value);
    }
}

public sealed class SubscriptionPlanToBrushValueConverter : IValueConverter
{
    public static SubscriptionPlanToBrushValueConverter Shared { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is SubscriptionPlan plan)
        {
            return plan switch
            {
                SubscriptionPlan.Free => Brushes.Gray,
                SubscriptionPlan.Starter => new SolidColorBrush(new Color(180, 0, 131, 255)),
                SubscriptionPlan.Plus => new SolidColorBrush(new Color(180, 155, 79, 255)),
                SubscriptionPlan.Pro => new SolidColorBrush(new Color(180, 255, 150, 0)),
                _ => Brushes.Transparent
            };
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}