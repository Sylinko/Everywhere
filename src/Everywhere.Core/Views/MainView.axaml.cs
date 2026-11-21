using Avalonia.Controls;
using Avalonia.Interactivity;
using Everywhere.Chat.Links;
using Everywhere.Common.Notification;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Views;

public partial class MainView : ReactiveUserControl<MainViewModel>
{
    private readonly NotificationCenter _notificationCenter;
    private readonly ChatLinkInteraction? _linkInteraction;
    private IDisposable? _notificationWindow;
    private Window? _linkInteractionWindow;

    public MainView(IServiceProvider serviceProvider, NotificationCenter notificationCenter) : base(serviceProvider, disposeOnUnloaded: false)
    {
        _notificationCenter = notificationCenter;
        _linkInteraction = serviceProvider.GetService<ChatLinkInteraction>();
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (TopLevel.GetTopLevel(this) is not Window window) return;

        _notificationWindow = _notificationCenter.RegisterPopupWindow(window, NotificationWindows.Main);
        if (_linkInteraction is null) return;

        window.SetValue(ChatLinkInteraction.InteractionProperty, _linkInteraction);
        _linkInteractionWindow = window;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (_linkInteractionWindow is { } window)
        {
            _linkInteractionWindow = null;
            window.ClearValue(ChatLinkInteraction.InteractionProperty);
        }

        DisposeHelper.DisposeToDefault(ref _notificationWindow);
        base.OnUnloaded(e);
    }
}
