using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Automation;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.ProcessIsolation.Automation;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Initialization;

/// <summary>
/// Initializes the chat window hotkey listener and preloads the chat window.
/// </summary>
public sealed class ChatWindowInitializer : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;
    private readonly IShortcutListener _shortcutListener;
    private readonly ChatVisualService _chatVisualService;
    private readonly ILogger<ChatWindowInitializer> _logger;
    private readonly Lock _syncLock = new();

    /// <summary>
    /// Initializes the chat window hotkey listener and preloads the chat window.
    /// </summary>
    /// <param name="serviceProvider"></param>
    /// <param name="settings"></param>
    /// <param name="shortcutListener"></param>
    /// <param name="chatVisualService"></param>
    /// <param name="logger"></param>
    public ChatWindowInitializer(
        IServiceProvider serviceProvider,
        Settings settings,
        IShortcutListener shortcutListener,
        ChatVisualService chatVisualService,
        ILogger<ChatWindowInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        _shortcutListener = shortcutListener;
        _chatVisualService = chatVisualService;
        _logger = logger;
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chatWindow = _serviceProvider.GetRequiredService<ChatWindow>();
        var chatWindowViewModel = chatWindow.ViewModel;
        var chatWindowHandle = chatWindow.TryGetPlatformHandle()?.Handle ?? 0;

        // Preload ChatWindow to avoid delay on first open
        chatWindow.Initialize();

        InitializeShortcut(
            _settings.Shortcut.ChatWindow,
            (shortcut, ref subscription) => RegisterChatWindowShortcut(chatWindow, chatWindowHandle, shortcut, ref subscription));
        InitializeShortcut(
            _settings.Shortcut.PickVisualElement,
            (shortcut, ref subscription) => RegisterPickElementShortcut(chatWindowViewModel, shortcut, ref subscription));
        InitializeShortcut(
            _settings.Shortcut.TakeScreenshot,
            (shortcut, ref subscription) => RegisterScreenshotShortcut(chatWindowViewModel, shortcut, ref subscription));

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
                        using (_syncLock.EnterScope())
                        {
                            DisposeHelper.DisposeToDefault(ref mainSubscription);
                            DisposeHelper.DisposeToDefault(ref alternativeSubscription);
                        }
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
            () => ResolveChatWindowTargetAsync(chatWindow, chatWindowHandle).Detach(_logger.ToExceptionHandler()),
            ref subscription);
    }

    private async Task ResolveChatWindowTargetAsync(ChatWindow chatWindow, nint chatWindowHandle)
    {
        VisualElementLocator? targetLocator;
        nint? nativeWindowHandle;
        try
        {
            var query = new VisualElementQueryRequest(VisualElementFields.NativeWindowHandle, 0);
            var snapshot = await _chatVisualService.ObserveElementAsync(VisualElementLocator.Focused, query: query);
            if (snapshot is { } focusedSnapshot)
            {
                targetLocator = VisualElementLocator.Focused;
                nativeWindowHandle = focusedSnapshot.NativeWindowHandle;
            }
            else
            {
                var pointerSnapshot = await _chatVisualService.ObserveElementAsync(
                    VisualElementLocator.Pointer,
                    VisualElementResolution.TopLevel,
                    query);
                nativeWindowHandle = pointerSnapshot?.NativeWindowHandle;
                targetLocator = nativeWindowHandle is > 0 and var handle ? VisualElementLocator.FromNativeWindow(handle) : null;
            }

            if (chatWindowHandle == nativeWindowHandle) targetLocator = null;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to resolve the visual target for the chat-window shortcut.");
            targetLocator = null;
            nativeWindowHandle = null;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (chatWindow.IsVisible && chatWindowHandle == nativeWindowHandle)
            {
                WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(true));
            }
            else
            {
                WeakReferenceMessenger.Default.Send(new ActivateChatSessionMessage(targetLocator));
            }
        });
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
            subscription = _shortcutListener.Register(shortcut, callback);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to register shortcut {Shortcut}", shortcut);
        }
    }
}