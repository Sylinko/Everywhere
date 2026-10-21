using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.StrategyEngine;
using Everywhere.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Everywhere.TextSelection;

/// <summary>
/// Owns the current selection and its business actions on the UI thread. The floating window
/// manages its own presentation and input; this service owns an attachment only when automatic
/// attachment capture did not adopt it.
/// </summary>
public sealed class TextSelectionToolbarService : IDisposable
{
    private readonly IServiceProvider _serviceProvider;
    private readonly Settings _settings;
    private readonly IOverlayInputWatcher _inputWatcher;
    private readonly IWindowHelper _windowHelper;
    private readonly TextSelectionToolbarActionsManager _actionsManager;
    private readonly ILogger<TextSelectionToolbarService> _logger;

    private TextSelectionToolbarWindow? _toolbar;
    private TextSelectionAttachment? _selection;
    private TextSelectionData? _observation;
    private bool _ownsSelection;

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
            if (!_settings.TextSelection.IsToolbarEnabled ||
                !observation.IsCurrent || observation.PointerPosition is not { } position ||
                _settings.TextSelection.Platform.IsToolbarExcluded(observation.ApplicationIdentity))
            {
                Hide();
                return;
            }

            var items = _actionsManager.GetMatchingItems(selection);
            if (_toolbar is null)
            {
                _toolbar = new TextSelectionToolbarWindow(_windowHelper, _inputWatcher, _settings);
                _toolbar.ActionInvoked += HandleActionInvoked;
                _toolbar.SubmitFreeformChat = SubmitFreeformChat;
                _toolbar.ExcludeApplicationRequested += ExcludeApplication;
                _toolbar.Hidden += ReleaseSelection;
            }

            if (!_toolbar.ShowFor(items, position, !string.IsNullOrEmpty(observation.ApplicationIdentity)))
            {
                Hide();
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
        if (!_ownsSelection && _selection is { } selection && !attachments.Contains(selection))
        {
            Hide();
        }
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

    private bool SubmitFreeformChat(string text, CustomAssistant assistant)
    {
        if (_selection is not { } selection || _observation?.IsCurrent != true)
        {
            Hide();
            return false;
        }

        var isAccepted = false;
        try
        {
            var chatWindow = _serviceProvider.GetRequiredService<ChatWindow>();
            var message = new UserChatMessage(text, [selection]);
            isAccepted = chatWindow.ViewModel.SendTextSelectionMessage(message, assistant);
            if (!isAccepted)
            {
                return false;
            }

            // Acceptance transfers ownership. Removing a borrowed selection may already have
            // hidden the toolbar through ValidateChatSelection; owned selections leave this service here.
            _selection = null;
            _ownsSelection = false;
            Hide();
            WeakReferenceMessenger.Default.Send(new CloakChatWindowMessage(false));
            _windowHelper.BringToForeground(chatWindow);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to submit text selection chat.");
            return isAccepted;
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
        // Only settings that invalidate the business session belong here. Presentation settings
        // are bound by the window and do not require this service to refresh its controls.
        if (e.PropertyName is not (null or "" or nameof(TextSelectionSettings.IsToolbarEnabled) or
            nameof(TextSelectionApplicationSettings.ToolbarExcludedApplications)))
        {
            return;
        }

        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_settings.TextSelection.IsToolbarEnabled ||
                _settings.TextSelection.Platform.IsToolbarExcluded(_observation?.ApplicationIdentity))
            {
                Hide();
            }
        });
    }

    /// <summary>
    /// Hides the toolbar and releases only selections that have not been adopted by the chat.
    /// </summary>
    public void Hide()
    {
        _toolbar?.Hide();
        ReleaseSelection();
    }

    private void ReleaseSelection()
    {
        var selection = _selection;
        var shouldDisposeSelection = _ownsSelection;
        _observation = null;
        _selection = null;
        _ownsSelection = false;

        if (shouldDisposeSelection)
        {
            selection?.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _settings.TextSelection.PropertyChanged -= HandleSettingsChanged;
        _settings.TextSelection.Platform.PropertyChanged -= HandleSettingsChanged;
        Dispatcher.UIThread.PostOnDemand(Hide);
    }
}