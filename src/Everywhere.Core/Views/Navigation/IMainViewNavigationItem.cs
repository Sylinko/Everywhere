using Tabler.Avalonia;

namespace Everywhere.Views;

public interface IMainViewNavigationItem
{
    int Index { get; }

    string RouteKey => GetType().Name;

    TablerIconKind Icon { get; }

    IDynamicLocaleKey TitleKey { get; }
}