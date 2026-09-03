using Avalonia;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Initialization;

/// <summary>
/// Owns the text selection toolbar: subscribes to text selections while the feature is enabled, and
/// shows the toolbar next to the selection with the strategies that match it.
/// </summary>
/// <remarks>
/// <para>
/// Subscription is driven by <see cref="TextSelectionToolbarSettings.IsEnabled"/> so that no input hook
/// is installed while the feature is off. Because platform <see cref="IVisualElementContext"/>
/// implementations reference-count their subscribers, this composes with the separate subscription
/// <see cref="ChatWindowInitializer"/> makes for attachment capture: hooks live while either consumer
/// wants them and are removed when neither does.
/// </para>
/// <para>
/// The toolbar window itself is created lazily on first use and then reused, so the visual tree is
/// built once rather than per selection.
/// </para>
/// </remarks>
public sealed class TextSelectionToolbarInitializer(
    IServiceProvider serviceProvider,
    Settings settings,
    IVisualElementContext visualElementContext,
    IOverlayDismissWatcher dismissWatcher,
    IWindowHelper windowHelper,
    IStrategyEngine strategyEngine,
    ILogger<TextSelectionToolbarInitializer> logger
) : IAsyncInitializer, IObserver<TextSelectionData>
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    private readonly Lock _syncLock = new();

    private IDisposable? _selectionSubscription;
    private IOverlayDismissWatch? _dismissWatch;
    private TextSelectionToolbarWindow? _toolbar;

    /// <summary>
    /// The selection the visible toolbar currently describes. UI-thread only.
    /// Held in a field rather than captured by the <see cref="TextSelectionToolbarWindow.ActionInvoked"/>
    /// handler, which is subscribed once for the lifetime of the reused window and would otherwise keep
    /// acting on the first selection forever.
    /// </summary>
    private TextSelectionData _currentSelection;

    public Task InitializeAsync()
    {
        settings.TextSelectionToolbar.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(TextSelectionToolbarSettings.IsEnabled))
            {
                HandleIsEnabledChanged(settings.TextSelectionToolbar.IsEnabled);
            }
        };
        HandleIsEnabledChanged(settings.TextSelectionToolbar.IsEnabled);

        return Task.CompletedTask;
    }

    private void HandleIsEnabledChanged(bool isEnabled)
    {
        using var _ = _syncLock.EnterScope();

        DisposeHelper.DisposeToDefault(ref _selectionSubscription);

        if (!isEnabled)
        {
            Dispatcher.UIThread.PostOnDemand(HideToolbar);
            return;
        }

        if (!dismissWatcher.IsSupported)
        {
            // Without a dismissal signal the toolbar could not be closed, so do not offer it at all.
            logger.LogInformation(
                "Text selection toolbar is enabled but {Watcher} is unsupported on this platform; not subscribing.",
                nameof(IOverlayDismissWatcher));
            return;
        }

        _selectionSubscription = visualElementContext.Subscribe(this);

        // Worth a permanent log line: this is the moment global input hooks become installed, and the
        // feature is otherwise silent, so "is it armed?" is not answerable from behaviour alone.
        logger.LogInformation("Text selection toolbar armed; subscribed to text selections.");
    }

    void IObserver<TextSelectionData>.OnCompleted() { }

    void IObserver<TextSelectionData>.OnError(Exception error) { }

    /// <remarks>Arrives on a platform hook thread; everything after the dispatcher hop is UI-thread only.</remarks>
    void IObserver<TextSelectionData>.OnNext(TextSelectionData data)
    {
        if (data.Text.IsNullOrEmpty()) return;

        // Ignore selections inside Everywhere itself, including the chat window and the toolbar.
        if (data.Element?.ProcessId == Environment.ProcessId) return;

        var anchor = visualElementContext.PointerPosition;
        if (anchor is null) return;

        Dispatcher.UIThread.PostOnDemand(() => ShowToolbar(data, anchor.Value));
    }

    private void ShowToolbar(TextSelectionData data, PixelPoint anchor)
    {
        try
        {
            if (!settings.TextSelectionToolbar.IsEnabled) return;

            var strategies = ResolveStrategies(data);
            if (strategies.Count == 0)
            {
                // Hide rather than leave the previous toolbar up: its buttons still reference the earlier
                // selection, so clicking one would act on text the user has already replaced.
                HideToolbar();

                // Logged because it is otherwise indistinguishable from the toolbar never being armed.
                logger.LogDebug("No strategies matched the current text selection; not showing the toolbar.");
                return;
            }

            var toolbar = _toolbar;
            if (toolbar is null)
            {
                toolbar = _toolbar = new TextSelectionToolbarWindow();
                toolbar.ActionInvoked += OnActionInvoked;
            }

            // Replaces any previous selection: a new selection supersedes whatever the toolbar was showing.
            _currentSelection = data;

            toolbar.ShowActionLabels = settings.TextSelectionToolbar.ShowActionLabels;

            var bounds = toolbar.ShowFor(strategies, anchor);

            // Update rather than replace: recreating the watch would reinstall the global input hooks on
            // every selection.
            if (_dismissWatch is { } existing) existing.Update(bounds);
            else _dismissWatch = dismissWatcher.Watch(bounds, () => Dispatcher.UIThread.PostOnDemand(HideToolbar));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to show the text selection toolbar.");
            HideToolbar();
        }
    }

    private IReadOnlyList<Strategy> ResolveStrategies(TextSelectionData data)
    {
        try
        {
            var context = StrategyContext.FromAttachments([new TextSelectionAttachment(data.Text!, data.Element)]);

            // Clamped here rather than trusted: the settings binder assigns persisted values without
            // applying the range declared by SettingsIntegerItemAttribute, so an edited settings file can
            // carry a count outside it.
            var maxActionCount = Math.Clamp(
                settings.TextSelectionToolbar.MaxActionCount,
                TextSelectionToolbarSettings.MinActionCount,
                TextSelectionToolbarSettings.MaxAllowedActionCount);

            // Ordered by descending priority, so taking the first N keeps the most relevant actions and
            // naturally excludes the negative-priority global strategies.
            return [..strategyEngine.GetStrategies(context).AsValueEnumerable().Take(maxActionCount)];
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve strategies for the current text selection.");
            return [];
        }
    }

    /// <remarks>
    /// The order here is load-bearing. Windows assigns the foreground window on mouse *down*, while this
    /// runs from Click on mouse *up*, so by now the toolbar is already the foreground window despite being
    /// non-activating. Hiding it first would leave the foreground pointing at a hidden window that cannot
    /// take keyboard focus — Windows does not reassign the foreground when a window is hidden — and the
    /// result is a state where no window receives input at all. So the chat window takes the foreground
    /// first, and only then is the toolbar hidden.
    /// </remarks>
    private void OnActionInvoked(Strategy strategy)
    {
        var selection = _currentSelection;

        try
        {
            var chatWindow = serviceProvider.GetRequiredService<ChatWindow>();
            chatWindow.ViewModel.RunStrategyOnTextSelection(selection, strategy);

            if (!windowHelper.BringToForeground(chatWindow))
            {
                logger.LogWarning(
                    "Chat window did not become the foreground window after a text selection action; " +
                    "keyboard input may not reach it.");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to run strategy {StrategyId} on the current text selection.", strategy.Id);
        }

        // Hidden last, and on a later dispatcher frame, so the foreground has already moved away from it.
        Dispatcher.UIThread.Post(HideToolbar, DispatcherPriority.Background);
    }

    private void HideToolbar()
    {
        // Disposes the watch, which uninstalls the global input hooks: nothing is observed while the
        // toolbar is hidden.
        DisposeHelper.DisposeToDefault(ref _dismissWatch);
        _toolbar?.Hide();
        _currentSelection = default;
    }
}
