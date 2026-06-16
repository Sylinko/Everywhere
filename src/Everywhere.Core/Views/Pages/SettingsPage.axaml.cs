using Avalonia.Controls;
using Everywhere.Configuration;
using Lucide.Avalonia;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Views.Pages;

/// <summary>
/// A hub control representing a settings category page, which displays a list of items that can be navigated to.
/// </summary>
public sealed partial class SettingsPage : UserControl, IMainViewNavigationTopLevelItemWithSubItems
{
    public int Index => 100;

    public LucideIconKind Icon => LucideIconKind.Cog;

    public IDynamicLocaleKey TitleKey { get; } = new DynamicLocaleKey(LocaleKey.SettingsPage_Title);

    public Item[] Items { get; }

    public SettingsPage(IServiceProvider serviceProvider)
    {
        var settings = serviceProvider.GetRequiredService<Settings>();
        Items =
        [
            MakeItem(settings.Common),
            MakeItem(settings.Display),
            MakeItem(settings.Shortcut),
            MakeItem(settings.Proxy),
            MakeItem(settings.ChatWindow),
            MakeItem(settings.SystemAssistant)
        ];

        InitializeComponent();

        static Item MakeItem(ISettingsCategory category) => new(category, new SettingsCategoryPage(category));
    }

    public IEnumerable<IMainViewNavigationSubItem> CreateSubItems() => Items.Select(i => i.Page);

    public sealed record Item(ISettingsCategory Category, SettingsCategoryPage Page);
}