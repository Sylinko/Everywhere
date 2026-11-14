using Avalonia.Controls;
using Avalonia.Interactivity;
using Everywhere.Common.Notification;
using Everywhere.Utilities;

namespace Everywhere.Views;

public partial class MainView : ReactiveUserControl<MainViewModel>
{
    private readonly NotificationCenter _notificationCenter;
    private IDisposable? _notificationWindow;

    public MainView(IServiceProvider serviceProvider, NotificationCenter notificationCenter) : base(serviceProvider, disposeOnUnloaded: false)
    {
        _notificationCenter = notificationCenter;
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (TopLevel.GetTopLevel(this) is Window window)
        {
            _notificationWindow = _notificationCenter.RegisterPopupWindow(window, NotificationWindows.Main);
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _notificationWindow);
        base.OnUnloaded(e);
    }
}