using Avalonia.Controls;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;

namespace Everywhere.Views;

public partial class ChatPluginDisplayBlockPresenter : ContentControl
{
    /// <summary>
    /// Defines the captured template input while Content retains the source identity.
    /// </summary>
    public static readonly DirectProperty<ChatPluginDisplayBlockPresenter, object?> SnapshotProperty =
        AvaloniaProperty.RegisterDirect<ChatPluginDisplayBlockPresenter, object?>(nameof(Snapshot), control => control.Snapshot);

    /// <summary>
    /// Gets captured mutable values, or the source itself for an immutable display block.
    /// </summary>
    public object? Snapshot
    {
        get => _snapshot;
        private set => SetAndRaise(SnapshotProperty, ref _snapshot, value);
    }

    private object? _snapshot;
    private ChatTurn? _turn;
    private ChatPluginDisplayBlock? _standaloneBlock;
    private int _isRefreshQueued;

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ObserveSource();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ReleaseSource();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty)
        {
            ReleaseSource();
            Snapshot = ChatDisplayBlockState.Capture(Content);
            if (VisualRoot is not null)
            {
                ObserveSource();
            }
        }
    }

    private void ObserveSource()
    {
        // Only realized mutable details observe changes. Static blocks and Markdown/terminal
        // streams keep their specialized channels; no historical block subscription graph exists.
        if (Content is not (ChatPluginContainerDisplayBlock or ChatPluginProgressDisplayBlock or
            ChatPluginFileReferencesDisplayBlock or ChatPluginFileDifferenceDisplayBlock or ChatPluginSubagentDisplayBlock))
        {
            return;
        }

        var block = (ChatPluginDisplayBlock)Content;
        if (block.Owner?.Turn is { } turn)
        {
            _turn = turn;
            turn.Invalidated += HandleInvalidated;
        }
        else
        {
            _standaloneBlock = block;
            block.Invalidated += HandleInvalidated;
        }

        Snapshot = ChatDisplayBlockState.Capture(Content);
    }

    private void ReleaseSource()
    {
        if (_turn is not null)
        {
            _turn.Invalidated -= HandleInvalidated;
            _turn = null;
        }

        if (_standaloneBlock is not null)
        {
            _standaloneBlock.Invalidated -= HandleInvalidated;
            _standaloneBlock = null;
        }
    }

    private void HandleInvalidated(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _isRefreshQueued, 1) != 0)
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _isRefreshQueued, 0);
            if (VisualRoot is not null)
            {
                Snapshot = ChatDisplayBlockState.Capture(Content);
            }
        });
    }

    /// <summary>
    /// Opens a URL from a detailed display block. This presenter is also used by confirmation UI
    /// outside <see cref="ChatMessageItemsControl"/>, so it cannot depend on the chat-root command.
    /// </summary>
    [RelayCommand]
    private static Task<bool> OpenUrlAsync(string url) => App.Launcher.LaunchUriAsync(new Uri(url));
}