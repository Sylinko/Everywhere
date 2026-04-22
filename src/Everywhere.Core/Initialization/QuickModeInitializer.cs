using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.StrategyEngine;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.Initialization;

public sealed class QuickModeInitializer(
    IServiceProvider serviceProvider,
    Settings settings,
    IShortcutListener shortcutListener,
    IVisualElementContext visualElementContext,
    ILogger<QuickModeInitializer> logger
) : IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly Lock _syncLock = new();

    private IDisposable? _quickModeShortcutSubscription;

    public Task InitializeAsync()
    {
        var floatingIslandWindow = serviceProvider.GetRequiredService<FloatingIslandWindow>();
        floatingIslandWindow.Show();

        settings.Shortcut.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ShortcutSettings.QuickMode))
            {
                HandleQuickModeShortcutChanged(settings.Shortcut.QuickMode);
            }
        };

        HandleQuickModeShortcutChanged(settings.Shortcut.QuickMode);

        return Task.CompletedTask;
    }

    private void HandleQuickModeShortcutChanged(KeyboardShortcut shortcut)
    {
        RegisterShortcutListener(
            shortcut,
            () => Dispatcher.UIThread.Post(() =>
                visualElementContext.SelectMultipleVisualElementsAsync(null)
                    .ContinueWith(
                        t =>
                        {
                            if (t.IsCompletedSuccessfully)
                            {
                                var elements = new List<QuickModeVisualElement>();
                                Task.Run(async () =>
                                {
                                    foreach (var visualElement in t.Result.Take(20))
                                    {
                                        var attachment = VisualElementAttachment.FromVisualElement(visualElement);

                                        try
                                        {
                                            using var data = await visualElement.CaptureAsync(CancellationToken.None);
                                            elements.Add(new QuickModeVisualElement(attachment, data.ToAvaloniaBitmap()));
                                        }
                                        catch
                                        {
                                            elements.Add(new QuickModeVisualElement(attachment, null));
                                        }
                                    }

                                    var context = StrategyContext.FromAttachments(elements.Select(e => e.Attachment).ToList());
                                    return serviceProvider.GetRequiredService<IStrategyEngine>().GetStrategies(context);
                                }).ContinueWith(
                                    tt =>
                                    {
                                        new QuickModeActionOverlay(elements, tt.Result).Show();
                                    },
                                    TaskScheduler.FromCurrentSynchronizationContext());
                            }
                        },
                        TaskScheduler.FromCurrentSynchronizationContext())),
            ref _quickModeShortcutSubscription);
    }

    private void RegisterShortcutListener(KeyboardShortcut shortcut, Action callback, ref IDisposable? subscription)
    {
        using var _ = _syncLock.EnterScope();

        subscription?.Dispose();
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
}