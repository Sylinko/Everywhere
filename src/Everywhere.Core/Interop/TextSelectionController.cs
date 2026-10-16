using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Configuration;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Interop;

/// <summary>
/// Identifies the independently configurable consumers of automatic text selection.
/// </summary>
public enum TextSelectionFeature
{
    AutomaticAttachment,
    Toolbar
}

/// <summary>
/// Requests an acknowledged change to one consumer's monitoring intent.
/// </summary>
public sealed record TextSelectionFeatureChange(TextSelectionFeature Feature, bool IsEnabled);

/// <summary>
/// Serializes consumer changes and keeps native monitoring active while either consumer needs it.
/// Settings are committed only after the Host acknowledges the effective state.
/// </summary>
public sealed class TextSelectionController : ObservableObject, IDisposable
{
    /// <summary>
    /// Gets the automatic attachment preference, independently of the toolbar.
    /// </summary>
    public bool IsAutomaticAttachmentEnabled => _settings.TextSelection.IsAutomaticAttachmentEnabled;

    /// <summary>
    /// Gets the persisted toolbar preference, including when imported onto an unsupported platform.
    /// </summary>
    public bool IsToolbarEnabled => _settings.TextSelection.IsToolbarEnabled;

    /// <summary>
    /// Gets the effective monitoring intent for consumers supported on this platform.
    /// </summary>
    public bool IsMonitoringRequested => IsAutomaticAttachmentEnabled || (IsToolbarEnabled && _settings.TextSelection.IsToolbarSupported);

    /// <summary>
    /// Gets the shared operation whose running state disables both settings switches.
    /// </summary>
    public IAsyncRelayCommand<TextSelectionFeatureChange> SetEnabledCommand { get; }

    private readonly Settings _settings;
    private readonly ITextSelectionWatcher _watcher;

    public TextSelectionController(Settings settings, ITextSelectionWatcher watcher)
    {
        _settings = settings;
        _watcher = watcher;
        SetEnabledCommand = new AsyncRelayCommand<TextSelectionFeatureChange>(SetEnabledAsync);
        watcher.RestorationRejected += HandleRestorationRejected;
    }

    private async Task SetEnabledAsync(TextSelectionFeatureChange? change)
    {
        if (change is null) return;
        if (change.Feature == TextSelectionFeature.Toolbar && change.IsEnabled && !_settings.TextSelection.IsToolbarSupported) return;

        var isAttachmentEnabled = change.Feature == TextSelectionFeature.AutomaticAttachment ? change.IsEnabled : IsAutomaticAttachmentEnabled;
        var isToolbarEnabled = change.Feature == TextSelectionFeature.Toolbar ? change.IsEnabled : IsToolbarEnabled;
        var shouldMonitor = isAttachmentEnabled || (isToolbarEnabled && _settings.TextSelection.IsToolbarSupported);

        try
        {
            await _watcher.SetEnabledCommand.ExecuteAsync(shouldMonitor);
        }
        catch
        {
            // Disabling the final consumer stops local delivery even if the Host cannot acknowledge it.
            if (!shouldMonitor) Commit(change);
            throw;
        }

        Commit(change);
    }

    private void Commit(TextSelectionFeatureChange change)
    {
        if (change.Feature == TextSelectionFeature.AutomaticAttachment)
        {
            _settings.TextSelection.IsAutomaticAttachmentEnabled = change.IsEnabled;
            OnPropertyChanged(nameof(IsAutomaticAttachmentEnabled));
        }
        else
        {
            _settings.TextSelection.IsToolbarEnabled = change.IsEnabled;
            OnPropertyChanged(nameof(IsToolbarEnabled));
        }

        OnPropertyChanged(nameof(IsMonitoringRequested));
    }

    private void HandleRestorationRejected(TextSelectionMonitoringControlResult result)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            // Neither consumer can remain advertised as active after native monitoring was rejected.
            Commit(new TextSelectionFeatureChange(TextSelectionFeature.AutomaticAttachment, false));
            if (_settings.TextSelection.IsToolbarSupported)
            {
                Commit(new TextSelectionFeatureChange(TextSelectionFeature.Toolbar, false));
            }
        });
    }

    /// <inheritdoc />
    public void Dispose() => _watcher.RestorationRejected -= HandleRestorationRejected;
}