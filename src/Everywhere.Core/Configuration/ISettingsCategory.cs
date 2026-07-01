using Everywhere.Views;
using Everywhere.Views.Pages;

namespace Everywhere.Configuration;

public interface ISettingsCategory : IMainViewNavigationSubItem
{
    Type IMainViewNavigationSubItem.GroupType => typeof(SettingsPage);

    SettingsItems SettingsItems { get; }
}