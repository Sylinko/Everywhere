namespace Everywhere.Views;

public interface IMainViewNavigationSubItem : IMainViewNavigationItem
{
    Type GroupType { get; }

    IDynamicLocaleKey? DescriptionKey { get; }
}