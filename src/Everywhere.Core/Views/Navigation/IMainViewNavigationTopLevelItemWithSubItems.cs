namespace Everywhere.Views;

public interface IMainViewNavigationTopLevelItemWithSubItems : IMainViewNavigationTopLevelItem
{
    IEnumerable<IMainViewNavigationSubItem> CreateSubItems();
}