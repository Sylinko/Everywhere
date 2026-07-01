using Lucide.Avalonia;

namespace Everywhere.Views;

public interface IMainViewNavigationItem
{
    int Index { get; }

    string RouteKey => GetType().Name;

    LucideIconKind Icon { get; }

    IDynamicLocaleKey TitleKey { get; }
}