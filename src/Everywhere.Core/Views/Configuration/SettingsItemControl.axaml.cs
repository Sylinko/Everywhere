using Avalonia.Controls;
using Everywhere.Configuration;

namespace Everywhere.Views;

public sealed class SettingsItemControl : Expander
{
    public static readonly StyledProperty<bool> IsExpandableProperty =
        AvaloniaProperty.Register<SettingsItemControl, bool>(nameof(IsExpandable));

    public bool IsExpandable
    {
        get => GetValue(IsExpandableProperty);
        set => SetValue(IsExpandableProperty, value);
    }

    public static readonly StyledProperty<SettingsItem?> ItemProperty =
        AvaloniaProperty.Register<SettingsItemControl, SettingsItem?>(nameof(Item));

    public SettingsItem? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }
}