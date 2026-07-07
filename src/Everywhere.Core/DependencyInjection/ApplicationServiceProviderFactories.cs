using Avalonia.Controls.ApplicationLifetimes;
using Everywhere.Views;
using ShadUI;
using ZLinq;

namespace Everywhere.DependencyInjection;

public static class ApplicationServiceProviderFactories
{
    public static DialogManager CreateDialogManager() => TryGetReactiveHost()?.DialogHost.Manager ?? new DialogManager();

    public static ToastHost CreateToastHost() => TryGetReactiveHost()?.ToastHost ?? new ToastHost();

    private static IReactiveHost? TryGetReactiveHost()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime lifetime) return null;
        return lifetime.Windows.AsValueEnumerable().FirstOrDefault(w => w.IsActive) as IReactiveHost ??
            lifetime.MainWindow as IReactiveHost ??
            lifetime.Windows.AsValueEnumerable().OfType<IReactiveHost>().FirstOrDefault();
    }
}