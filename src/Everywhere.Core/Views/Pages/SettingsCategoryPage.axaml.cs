using Avalonia.Controls;
using Everywhere.Configuration;
using Lucide.Avalonia;

namespace Everywhere.Views.Pages;

/// <summary>
/// Represents a settings category page that displays a list of settings items.
/// It dynamically creates settings items based on the properties of a specified settings category.
/// </summary>
public sealed partial class SettingsCategoryPage : UserControl, IMainViewNavigationSubItem
{
    public int Index => _settingsCategory.Index;

    public string RouteKey => _settingsCategory.RouteKey;

    public LucideIconKind Icon => _settingsCategory.Icon;

    public IDynamicLocaleKey TitleKey => _settingsCategory.TitleKey;

    public SettingsItems SettingItems => _settingsCategory.SettingsItems;

    public Type GroupType => _settingsCategory.GroupType;

    public IDynamicLocaleKey? DescriptionKey => _settingsCategory.DescriptionKey;

    private readonly ISettingsCategory _settingsCategory;

    public SettingsCategoryPage(ISettingsCategory settingsCategory)
    {
        _settingsCategory = settingsCategory;
        InitializeComponent();
    }
}