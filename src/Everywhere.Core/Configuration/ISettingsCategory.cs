using Everywhere.Views;
using Everywhere.Views.Pages;

namespace Everywhere.Configuration;

public interface ISettingsCategory : IMainViewNavigationSubItem, IHaveSettingsItems
{
    Type IMainViewNavigationSubItem.GroupType => typeof(SettingsPage);
}