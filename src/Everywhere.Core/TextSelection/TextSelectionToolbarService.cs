using System.ComponentModel;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.StrategyEngine;
using Everywhere.Utilities;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.TextSelection;

/// <summary>
/// Owns the floating toolbar on the UI thread. The chat observer supplies its one selection;
/// the toolbar owns an attachment only when automatic attachment capture did not adopt it.
/// </summary>
public sealed class TextSelectionToolbarService : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;
    private readonly IOverlayInputWatcher _inputWatcher;
    private readonly IWindowHelper _windowHelper;
    private readonly TextSelectionToolbarActionsManager _actionsManager;
    private readonly ILogger<TextSelectionToolbarService> _logger;
    private IOverlayInputWatch? _inputWatch;
    private TextSelectionToolbarWindow? _toolbar;
    private TextSelectionAttachment? _selection;
    private TextSelectionData? _observation;
    private PixelRect _toolbarBounds;
    private PixelRect? _menuBounds;
    private bool _ownsSelection;
    private int _generation;

    // Published on the UI thread; captures geometry without exposing mutable UI state to native hooks.
    private Action<PixelPoint>? _updateOpacityFactor;

    public TextSelectionToolbarService(
        IServiceProvider serviceProvider,
        Settings settings,
        IOverlayInputWatcher inputWatcher,
        IWindowHelper windowHelper,
        TextSelectionToolbarActionsManager actionsManager,
        ILogger<TextSelectionToolbarService> logger)
    {
        _serviceProvider = serviceProvider;
        _settings = settings;
        _inputWatcher = inputWatcher;
        _windowHelper = windowHelper;
        _actionsManager = actionsManager;
        _logger = logger;
        settings.TextSelection.PropertyChanged += HandleSettingsChanged;
        settings.TextSelection.Platform.PropertyChanged += HandleSettingsChanged;
    }

    /// <summary>
    /// Presents a selection, taking ownership when it does not already belong to the chat draft.
    /// The observation is retained only for its revision check; its source has already been moved.
    /// </summary>
    public void Show(TextSelectionData observation, TextSelectionAttachment selection, bool isChatOwned)
    {
        Hide();
        _selection = selection;
        _observation = observation;
        _ownsSelection = !isChatOwned;

        try
        {
            if (!_settings.TextSelection.IsToolbarEnabled || !_inputWatcher.IsSupported ||
                !observation.IsCurrent || observation.PointerPosition is not { } position ||
                _settings.TextSelection.Platform.IsToolbarExcluded(observation.ApplicationIdentity))
            {
                Hide();
                return;
            }

            var items = _actionsManager.GetMatchingItems(selection);
            if (items.Count == 0)
            {
                Hide();
                return;
            }

            if (_toolbar is null)
            {
                _toolbar = new TextSelectionToolbarWindow(_windowHelper);
                _toolbar.ActionInvoked += HandleActionInvoked;
                _toolbar.BoundsChanged += bounds =>
                {
                    _toolbarBounds = bounds;
                    _inputWatch?.Update(bounds, _menuBounds);
                    UpdateOpacity();
                };
                _toolbar.Toolbar.MenuBoundsChanged += bounds =>
                {
                    _menuBounds = bounds;
                    _inputWatch?.Update(_toolbarBounds, bounds);
                    UpdateOpacity();
                };
                _toolbar.Toolbar.CloseRequested += Hide;
                _toolbar.Toolbar.ExcludeApplicationRequested += ExcludeApplication;
            }

            _toolbar.Toolbar.VisibleActionCount = _settings.TextSelection.VisibleActionCount;
            _toolbar.Toolbar.CanExcludeApplication = !string.IsNullOrEmpty(observation.ApplicationIdentity);
            var transitions = _toolbar.Toolbar.Transitions;
            _toolbar.Toolbar.Transitions = null;
            try
            {
                // Set the initial value before the first rendered frame, without animating from an old selection.
                _toolbar.Toolbar.Opacity = _settings.TextSelection.ToolbarOpacity;
                _toolbarBounds = _toolbar.ShowFor(items, position);
                UpdateOpacity();

                var generation = _generation;
                _inputWatch = _inputWatcher.Watch(
                    _toolbarBounds,
                    () => Dispatcher.UIThread.Post(() =>
                    {
                        // An already queued callback must never dismiss a newer selection.
                        if (generation == _generation) Hide();
                    }),
                    HandlePointerMoved);
                UpdateOpacity();
            }
            finally
            {
                _toolbar.Toolbar.Transitions = transitions;
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to show the text selection toolbar.");
            Hide();
        }
    }

    /// <summary>
    /// Drops a borrowed selection as soon as the chat removes or sends its attachment.
    /// </summary>
    public void ValidateChatSelection(IEnumerable<ChatAttachment> attachments)
    {
        if (!_ownsSelection && _selection is { } selection && !attachments.Contains(selection)) Hide();
    }

    private void HandleActionInvoked(Strategy strategy)
    {
        if (_selection is not { } selection || _observation?.IsCurrent != true)
        {
            Hide();
            return;
        }

        // Ownership moves to the draft before any synchronous collection notifications can run.
        _selection = null;
        _ownsSelection = false;
        try
        {
            var chatWindow = _serviceProvider.GetRequiredService<ChatWindow>();
            chatWindow.ViewModel.RunStrategyOnTextSelection(selection, strategy);
            _windowHelper.BringToForeground(chatWindow);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to run text selection strategy {StrategyId}.", strategy.Id);
        }
        finally
        {
            Hide();
        }
    }

    private void ExcludeApplication()
    {
        // Use the identity observed with the selection; opening the menu may change the foreground window.
        if (_observation is not { IsCurrent: true, ApplicationIdentity: { Length: > 0 } identity })
        {
            Hide();
            return;
        }

        var platform = _settings.TextSelection.Platform;
        if (!platform.IsToolbarExcluded(identity))
        {
            platform.ToolbarExcludedApplications = [.. platform.ToolbarExcludedApplications, identity];
        }

        Hide();
    }

    private void HandleSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_settings.TextSelection.IsToolbarEnabled ||
                _settings.TextSelection.Platform.IsToolbarExcluded(_observation?.ApplicationIdentity))
            {
                Hide();
                return;
            }

            if (_toolbar is null) return;
            _toolbar.Toolbar.VisibleActionCount = _settings.TextSelection.VisibleActionCount;
            UpdateOpacity();
        });
    }

    private void UpdateOpacity()
    {
        if (_toolbar is not { IsVisible: true } toolbar || _observation is null)
        {
            return;
        }

        if (!_settings.TextSelection.IsToolbarOpacityAdaptive)
        {
            Volatile.Write(ref _updateOpacityFactor, null);
            toolbar.Toolbar.Opacity = _settings.TextSelection.ToolbarOpacity;
            return;
        }

        // BoundsChanged also covers ScalingChanged. Read geometry only when it or settings change.
        var scale = toolbar.DesktopScaling;
        var bounds = _toolbarBounds.ToRect(scale);
        var menuBounds = _menuBounds?.ToRect(scale);
        var opacityStep = (_inputWatcher.PointerPosition ?? _observation.PointerPosition) is { } pointer ? CalculateStep(pointer) : 20;
        var isUpdateQueued = 0;

        Volatile.Write(ref _updateOpacityFactor, UpdateFactor);
        toolbar.Toolbar.Opacity = _settings.TextSelection.ToolbarOpacity * opacityStep / 20d;

        int CalculateStep(PixelPoint pointerPoint)
        {
            var point = new Point(pointerPoint.X / scale, pointerPoint.Y / scale);
            return (int)Math.Round(TextSelectionToolbar.CalculateOpacityFactor(point, bounds, menuBounds) * 20d);
        }

        void UpdateFactor(PixelPoint position)
        {
            var step = CalculateStep(position);
            // ReSharper disable once AccessToModifiedClosure
            if (Interlocked.Exchange(ref opacityStep, step) == step)
            {
                return;
            }

            if (Interlocked.Exchange(ref isUpdateQueued, 1) != 0)
            {
                return;
            }

            // Each geometry revision owns its latest step; obsolete queued callbacks cannot overwrite a new one.
            Dispatcher.UIThread.Post(() =>
            {
                Interlocked.Exchange(ref isUpdateQueued, 0);
                if (_updateOpacityFactor != UpdateFactor || !toolbar.IsVisible)
                {
                    return;
                }

                toolbar.Toolbar.Opacity = _settings.TextSelection.ToolbarOpacity * Volatile.Read(ref opacityStep) / 20d;
            });
        }
    }

    private void HandlePointerMoved(PixelPoint position)
    {
        Volatile.Read(ref _updateOpacityFactor)?.Invoke(position);
    }

    /// <summary>
    /// Hides the toolbar and releases only selections that have not been adopted by the chat.
    /// </summary>
    public void Hide()
    {
        _generation++;
        _observation = null;
        Volatile.Write(ref _updateOpacityFactor, null);

        DisposeHelper.DisposeToDefault(ref _inputWatch);
        _toolbar?.Toolbar.CloseMenu();
        _menuBounds = null;
        _toolbar?.Hide();
        if (_ownsSelection) _selection?.Dispose();
        _selection = null;
        _ownsSelection = false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.TextSelection.PropertyChanged -= HandleSettingsChanged;
        _settings.TextSelection.Platform.PropertyChanged -= HandleSettingsChanged;
        Dispatcher.UIThread.PostOnDemand(Hide);
    }
}