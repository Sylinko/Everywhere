using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using ShadUI;
using ZLinq;

namespace Everywhere.ViewModels;

public sealed partial class MainViewModel : ReactiveViewModelBase, IRecipient<MainViewNavigateMessage>
{
    [ObservableProperty] public partial NavigationBarItem? SelectedItem { get; set; }

    public IReadOnlyBindableList<NavigationBarItem> Items { get; }

    /// <summary>
    /// Use public property for MVVM binding
    /// </summary>
    public PersistentState PersistentState { get; }

    private readonly SourceList<NavigationBarItem> _itemsSource = new();
    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;

    private bool _isFirstLoad = true;

    public MainViewModel(
        IServiceProvider serviceProvider,
        Settings settings,
        PersistentState persistentState)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        PersistentState = persistentState;

        Items = _itemsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(LifetimeDisposables);
        LifetimeDisposables.Add(_itemsSource);
        InitializeNavigationBarItems();

        WeakReferenceMessenger.Default.Register(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            WeakReferenceMessenger.Default.UnregisterAll(this);
        }

        base.Dispose(disposing);
    }

    protected internal override async Task ViewLoaded(CancellationToken cancellationToken)
    {
        if (_isFirstLoad)
        {
            _isFirstLoad = false;
            ShowOobeDialogOnDemand();
        }

        await base.ViewLoaded(cancellationToken);
    }

    private void InitializeNavigationBarItems()
    {
        var allPages = _serviceProvider.GetServices<IMainViewNavigationItem>().AsValueEnumerable();
        var topLevelPages = allPages.OfType<IMainViewNavigationTopLevelItem>();
        var subPagesGroups = allPages.OfType<IMainViewNavigationSubItem>().GroupBy(p => p.GroupType);
        var rootItems = new List<(int Index, NavigationBarItem Item)>();

        foreach (var page in topLevelPages)
        {
            NavigationBarItem item;
            rootItems.Add(
                (page.Index, item = new NavigationBarItem
                {
                    Icon = page.Icon,
                    [!ContentControl.ContentProperty] = page.TitleKey.ToBinding(),
                    [!NavigationBarItem.ToolTipProperty] = page.TitleKey.ToBinding(),
                    Tag = page.RouteKey,
                    Route = page,
                }));

            if (page is IMainViewNavigationTopLevelItemWithSubItems factory)
            {
                item.Children.AddRange(
                    factory.CreateSubItems().Select(i => new NavigationBarItem
                    {
                        Icon = i.Icon,
                        [!ContentControl.ContentProperty] = i.TitleKey.ToBinding(),
                        [!NavigationBarItem.ToolTipProperty] = i.TitleKey.ToBinding(),
                        Tag = i.RouteKey,
                        Route = i
                    }));
            }
        }

        foreach (var group in subPagesGroups)
        {
            if (rootItems.AsValueEnumerable().FirstOrDefault(r => r.Item.Route?.GetType() == group.Key) is not (_, { } groupItem))
            {
                var topLevelItem = (IMainViewNavigationTopLevelItem)_serviceProvider.GetRequiredService(group.Key);
                groupItem = new NavigationBarItem
                {
                    Icon = topLevelItem.Icon,
                    [!ContentControl.ContentProperty] = topLevelItem.TitleKey.ToBinding(),
                    [!NavigationBarItem.ToolTipProperty] = topLevelItem.TitleKey.ToBinding(),
                    Tag = topLevelItem.RouteKey,
                    Route = topLevelItem
                };
                rootItems.Add((topLevelItem.Index, groupItem));
            }

            foreach (var subPage in group.AsValueEnumerable().OrderBy(p => p.Index))
            {
                var item = new NavigationBarItem
                {
                    [!ContentControl.ContentProperty] = subPage.TitleKey.ToBinding(),
                    [!NavigationBarItem.ToolTipProperty] = subPage.TitleKey.ToBinding(),
                    Tag = subPage.RouteKey,
                    Route = subPage,
                };
                groupItem.Children.Add(item);
            }
        }

        _itemsSource.AddRange(rootItems.OrderBy(x => x.Index).Select(x => x.Item));

        SelectedItem = FindNavigationBarItem(_itemsSource.Items, i => i.Route is not null);
    }

    private static NavigationBarItem? FindNavigationBarItem(IEnumerable<NavigationBarItem> items, Predicate<NavigationBarItem> match)
    {
        foreach (var item in items)
        {
            if (match(item)) return item;
            if (item.Children.Count <= 0) continue;

            var child = FindNavigationBarItem(item.Children, match);
            if (child != null) return child;
        }

        return null;
    }

    /// <summary>
    /// Shows the OOBE dialog if the application is launched for the first time or after an update.
    /// </summary>
    private void ShowOobeDialogOnDemand()
    {
        if (_settings.Model.CustomAssistants.Count == 0)
        {
            DialogManager
                .CreateCustomDialog(_serviceProvider.GetRequiredService<WelcomeView>())
                .ShowAsync()
                .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
        }
        else
        {
            var currentVersion = RuntimeConstants.Version;
            if (!SemanticVersion.TryParse(PersistentState.PreviousLaunchVersion, out var previousVersion))
            {
                previousVersion = new SemanticVersion(0);
            }

            if (previousVersion < currentVersion)
            {
                NavigateTo(_serviceProvider.GetRequiredService<ChangeLogView>());
                ToastHost
                    .CreateToast(LocaleResolver.MainViewModel_UpgradeSuccessfulToast_Title)
                    .WithDurationSeconds(5)
                    .ShowAsync()
                    .Detach(IExceptionHandler.DangerouslyIgnoreAllException);
            }
        }
    }

    [RelayCommand]
    private void NavigateToType(Type routeType)
    {
        var item = FindNavigationBarItem(
            _itemsSource.Items,
            i => i.Route?.GetType() == routeType || i.Children.Any(c => c.Route?.GetType() == routeType));
        if (item != null) SelectedItem = item;
    }

    [RelayCommand]
    public void NavigateTo(object route)
    {
        if (route is string { Length: > 0 } routeString)
        {
            var parts = routeString.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var items = _itemsSource.Items;
            for (var index = 0; index < parts.Length; index++)
            {
                var part = parts[index];
                var item = items.FirstOrDefault(i => string.Equals(part.Trim(), i.Tag as string, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    Log.ForContext<MainViewModel>().Warning("Failed to navigate to route {Route} because part {Part} was not found", route, part);
                    break;
                }

                if (index == parts.Length - 1)
                {
                    SelectedItem = item;
                    break;
                }

                items = item.Children;
            }
        }
        else
        {
            var item = FindNavigationBarItem(_itemsSource.Items, i => i.Route == route);
            SelectedItem = item ?? new NavigationBarItem(route); // This allows navigating to a route that is not in the navigation bar
        }
    }

    protected internal override Task ViewUnloaded()
    {
        ShowHideToTrayNotificationOnDemand();

        return base.ViewUnloaded();
    }

    private void ShowHideToTrayNotificationOnDemand()
    {
        if (PersistentState.IsHideToTrayIconNotificationShown) return;

        ServiceLocator.Resolve<INativeHelper>().ShowDesktopNotificationAsync(LocaleResolver.MainView_EverywhereHasMinimizedToTray);
        PersistentState.IsHideToTrayIconNotificationShown = true;
    }

    void IRecipient<MainViewNavigateMessage>.Receive(MainViewNavigateMessage message)
    {
        if (message.Route is Type routeType) NavigateToType(routeType);
        else NavigateTo(message.Route);
    }

}
