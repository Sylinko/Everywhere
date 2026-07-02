using Avalonia.Controls;
using Everywhere.Common;
using Everywhere.Configuration;

namespace Everywhere.Views;

public sealed class SettingsControlPresenter : ContentControl
{
    public static readonly StyledProperty<SettingsControlItem?> ItemProperty =
        AvaloniaProperty.Register<SettingsControlPresenter, SettingsControlItem?>(nameof(Item));

    public SettingsControlItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ItemProperty)
        {
            Content = change.NewValue is SettingsControlItem item ?
                item.CreateControl(ServiceLocator.Resolve<IServiceProvider>()) :
                null;
        }
    }
}
