using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Utilities;
using Microsoft.Extensions.DependencyInjection;

namespace Everywhere.Views;

public sealed partial class MainTrayIcon : TrayIcon
{
    private readonly App _app;
    private readonly ShortcutSettings _shortcutSettings;
    private readonly DebounceExecutor<MainTrayIcon, DispatcherTimerImpl> _trayIconClickedDebounce;
    private int _trayIconClickCount;

    public MainTrayIcon(App app, IServiceProvider serviceProvider)
    {
        _app = app;
        _shortcutSettings = serviceProvider.GetRequiredService<Settings>().Shortcut;

        _trayIconClickedDebounce = new DebounceExecutor<MainTrayIcon, DispatcherTimerImpl>(
            () => this,
            sender =>
            {
                if (sender._trayIconClickCount >= 2) sender.ShowMainWindow();
                else ShowChatWindow();

                sender._trayIconClickCount = 0;
            },
            TimeSpan.FromMilliseconds(300)
        );

        AvaloniaXamlLoader.Load(this);
        InitializeMenuItems();
    }

    private void InitializeMenuItems()
    {
        Menu =
        [
            new NativeMenuItem
            {
                [!NativeMenuItem.HeaderProperty] = new DynamicResourceKey(LocaleKey.MainTrayIcon_Menu_OpenChatWindow).ToBinding(),
                Command = ShowChatWindowCommand
            },

            new NativeMenuItem
            {
                [!NativeMenuItem.HeaderProperty] = new DynamicResourceKey(LocaleKey.MainTrayIcon_Menu_OpenMainWindow).ToBinding(),
                Command = ShowMainWindowCommand
            },
        ];

#if DEBUG
        Menu.Items.Add(new NativeMenuItem
        {
            [!NativeMenuItem.HeaderProperty] = new DynamicResourceKey(LocaleKey.MainTrayIcon_Menu_OpenDebugWindow).ToBinding(),
            Command = ShowDebugWindowCommand
        });
#endif

        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(new NativeMenuItem
        {
            [!NativeMenuItem.HeaderProperty] = new DynamicResourceKey(LocaleKey.MainTrayIcon_Menu_EnableChatWindowShortcut).ToBinding(),
            [!NativeMenuItem.IsCheckedProperty] = new Binding
            {
                // TODO: Use CompiledBinding.Create in Avalonia 12
                Path = nameof(ShortcutSettings.ChatWindow.IsEnabled),
                Source = _shortcutSettings.ChatWindow,
                Mode = BindingMode.TwoWay
            },
            ToggleType = MenuItemToggleType.CheckBox,
        });

        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(new NativeMenuItem
        {
            [!NativeMenuItem.HeaderProperty] = new DynamicResourceKey(LocaleKey.MainTrayIcon_Menu_Exit).ToBinding(),
            Command = ExitCommand
        });
    }

    private void HandleTrayIconClicked(object? sender, EventArgs e)
    {
        _trayIconClickCount++;
        if (_trayIconClickCount >= 2)
        {
            // Double click detected, open main window immediately.
            ShowMainWindow();
            _trayIconClickCount = 0;
            _trayIconClickedDebounce.Cancel();
        }
        else
        {
            // Start or reset the debounce timer for single click.
            _trayIconClickedDebounce.Trigger();
        }
    }

    [RelayCommand]
    private static void ShowChatWindow() =>
        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage());

    [RelayCommand]
    private void ShowMainWindow() => _app.ShowMainWindow();

    [RelayCommand]
    private void ShowDebugWindow() => _app.ShowDebugWindow();

    [RelayCommand]
    private static void Exit() => Environment.Exit(0);
}