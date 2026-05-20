using Avalonia.Controls;
using Everywhere.Configuration;

namespace Everywhere.Views;

public sealed class SettingsGroupPanel : ItemsControl
{
    public static readonly StyledProperty<string?> HeaderProperty =
        AvaloniaProperty.Register<SettingsGroupPanel, string?>(nameof(Header));

    public string? Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        recycleKey = null;
        return item is not SettingsItemControl;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return item switch
        {
            SettingsItem => new SettingsItemControl(),
            _ => base.CreateContainerForItemOverride(item, index, recycleKey)
        };
    }

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        if (container is SettingsItemControl settingsItemControl && item is SettingsItem settingsItem)
        {
            settingsItemControl.Item = settingsItem;
            settingsItemControl.Classes.AddRange(settingsItem.Classes);
            settingsItemControl[!IsEnabledProperty] = settingsItem[!SettingsItem.IsEnabledProperty];
            settingsItemControl[!SettingsItemControl.IsExpandableProperty] = settingsItem[!SettingsItem.IsExpandableProperty];
            settingsItemControl[!Expander.IsExpandedProperty] = settingsItem[!SettingsItem.IsExpandedProperty]; // TODO: TwoWay
            return;
        }

        base.PrepareContainerForItemOverride(container, item, index);
    }
}
