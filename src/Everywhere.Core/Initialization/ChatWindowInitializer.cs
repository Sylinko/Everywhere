using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the chat window hotkey listener and preloads the chat window.
/// </summary>
/// <param name="settings"></param>
/// <param name="shortcutListener"></param>
/// <param name="visualElementContext"></param>
/// <param name="logger"></param>
public sealed class ChatWindowInitializer(
    IServiceProvider serviceProvider,
    Settings settings,
    IShortcutListener shortcutListener,
    IVisualElementContext visualElementContext,
    ILogger<ChatWindowInitializer> logger
) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly Lock _syncLock = new();

    private IDisposable? _textSelectionSubscription;

    public Task InitializeAsync()
    {
        var chatWindow = serviceProvider.GetRequiredService<ChatWindow>();
        var chatWindowViewModel = chatWindow.ViewModel;
        var chatWindowHandle = chatWindow.TryGetPlatformHandle()?.Handle ?? 0;

        // Preload ChatWindow to avoid delay on first open
        chatWindow.Initialize();

        InitializeShortcut(
            settings.Shortcut.ChatWindow,
            (shortcut, ref subscription) => RegisterChatWindowShortcut(chatWindow, chatWindowHandle, shortcut, ref subscription));
        InitializeShortcut(
            settings.Shortcut.PickVisualElement,
            (shortcut, ref subscription) => RegisterPickElementShortcut(chatWindowViewModel, shortcut, ref subscription));
        InitializeShortcut(
            settings.Shortcut.TakeScreenshot,
            (shortcut, ref subscription) => RegisterScreenshotShortcut(chatWindowViewModel, shortcut, ref subscription));

        settings.ChatWindow.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ChatWindowSettings.AutomaticallyAddTextSelection))
            {
                HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);
            }
        };
        HandleTextSelectionChanged(chatWindowViewModel, settings.ChatWindow.AutomaticallyAddTextSelection);

        return Task.CompletedTask;
    }

    private delegate void CompositeKeyboardShortcutRegister(KeyboardShortcut shortcut, ref IDisposable? subscription);

    private void InitializeShortcut(CompositeKeyboardShortcut shortcut, CompositeKeyboardShortcutRegister register)
    {
        IDisposable? mainSubscription = null, alternativeSubscription = null;

        shortcut.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(CompositeKeyboardShortcut.IsEnabled):
                {
                    if (shortcut.IsEnabled) RegisterAll();
                    else
                    {
                        using var _0 = _syncLock.EnterScope();

                        DisposeHelper.DisposeToDefault(ref mainSubscription);
                        DisposeHelper.DisposeToDefault(ref alternativeSubscription);
                    }

                    break;
                }
                case nameof(CompositeKeyboardShortcut.Main) when shortcut.IsEnabled:
                {
                    // ReSharper disable once AccessToModifiedClosure
                    register(shortcut.Main, ref mainSubscription);
                    break;
                }
                case nameof(CompositeKeyboardShortcut.Alternative) when shortcut.IsEnabled:
                {
                    // ReSharper disable once AccessToModifiedClosure
                    register(shortcut.Alternative, ref alternativeSubscription);
                    break;
                }
            }
        };

        if (shortcut.IsEnabled) RegisterAll();

        void RegisterAll()
        {
            if (shortcut.Main.IsValid) register(shortcut.Main, ref mainSubscription);
            if (shortcut.Alternative.IsValid) register(shortcut.Alternative, ref alternativeSubscription);
        }
    }

    private void RegisterChatWindowShortcut(ChatWindow chatWindow, nint chatWindowHandle, KeyboardShortcut shortcut, ref IDisposable? subscription)
    {
        RegisterShortcutListener(
            shortcut,
            () =>
            {
                IVisualElement? element;
                nint? hWnd;
                try
                {
                    element = visualElementContext.FocusedElement ??
                        visualElementContext.ElementFromPointer()?
                            .GetAncestors(true)
                            .LastOrDefault();
                    hWnd = element?.NativeWindowHandle;
                    if (chatWindowHandle == hWnd) element = null; // Don't allow to select itself
                }
                catch
                {
                    element = null;
                    hWnd = null;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (chatWindow.IsVisible && chatWindowHandle == hWnd)
                    {
                        WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true)); // Hide chat window if it's already focused
                    }
                    else
                    {
                        WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage(element));
                    }
                });
            },
            ref subscription);
    }

    private void RegisterPickElementShortcut(ChatWindowViewModel chatWindowViewModel, KeyboardShortcut shortcut, ref IDisposable? subscription)
    {
        RegisterShortcutListener(
            shortcut,
            () => Dispatcher.UIThread.Post(() => chatWindowViewModel.PickVisualElementCommand.Execute(null)),
            ref subscription);
    }

    private void RegisterScreenshotShortcut(ChatWindowViewModel chatWindowViewModel, KeyboardShortcut shortcut, ref IDisposable? subscription)
    {
        RegisterShortcutListener(
            shortcut,
            () => Dispatcher.UIThread.Post(() => chatWindowViewModel.TakeScreenshotCommand.Execute(null)),
            ref subscription);
    }

    private void RegisterShortcutListener(KeyboardShortcut shortcut, Action callback, ref IDisposable? subscription)
    {
        using var _ = _syncLock.EnterScope();

        DisposeHelper.DisposeToDefault(ref subscription);
        if (!shortcut.IsValid) return;

        try
        {
            subscription = shortcutListener.Register(shortcut, callback);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to register shortcut {Shortcut}", shortcut);
        }
    }

    private void HandleTextSelectionChanged(ChatWindowViewModel chatWindowViewModel, bool isEnabled)
    {
        using var _ = _syncLock.EnterScope();

        _textSelectionSubscription?.Dispose();
        if (isEnabled) _textSelectionSubscription = visualElementContext.Subscribe(chatWindowViewModel);
    }
}