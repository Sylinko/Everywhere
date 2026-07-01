using Avalonia.Controls;
using Avalonia.Controls.Metadata;
using Everywhere.Configuration;

namespace Everywhere.Views;

[PseudoClasses(":group-first", ":group-last")]
public sealed class SettingsItemControl : Expander
{
    internal const string GroupFirstPseudoClass = ":group-first";
    internal const string GroupLastPseudoClass = ":group-last";

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

    internal void SetGroupBoundaryState(bool isFirst, bool isLast)
    {
        PseudoClasses.Set(GroupFirstPseudoClass, isFirst);
        PseudoClasses.Set(GroupLastPseudoClass, isLast);
    }
}
