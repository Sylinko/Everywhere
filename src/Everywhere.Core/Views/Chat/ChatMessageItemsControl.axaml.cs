using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
using Everywhere.Chat;
using Everywhere.Chat.Plugins;
using Everywhere.Interactions;
using LiveMarkdown.Avalonia;
using Serilog;
using ShadUI;

namespace Everywhere.Views;

public sealed partial class ChatMessageItemsControl : ItemsControl
{
    /// <summary>
    /// Defines the <see cref="TextSearch"/> property.
    /// </summary>
    public static readonly StyledProperty<ChatTextSearchViewModel?> TextSearchProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, ChatTextSearchViewModel?>(nameof(TextSearch));

    /// <summary>
    /// Gets or sets the current-conversation text-search coordinator.
    /// </summary>
    public ChatTextSearchViewModel? TextSearch
    {
        get => GetValue(TextSearchProperty);
        set => SetValue(TextSearchProperty, value);
    }

    public ChatTextSearchSurfaceRegistry TextSearchSurfaceRegistry { get; } = new();

    /// <summary>
    /// Defines the <see cref="ChatContext"/> property.
    /// </summary>
    public static readonly StyledProperty<ChatContext?> ChatContextProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, ChatContext?>(nameof(ChatContext));

    /// <summary>
    /// Gets or sets the chat context whose selected branch is projected incrementally.
    /// </summary>
    public ChatContext? ChatContext
    {
        get => GetValue(ChatContextProperty);
        set => SetValue(ChatContextProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="IsReadonly"/> property.
    /// </summary>
    public static readonly StyledProperty<bool> IsReadonlyProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, bool>(nameof(IsReadonly));

    /// <summary>
    /// Gets or sets a value indicating whether the control is in read-only mode.
    /// </summary>
    public bool IsReadonly
    {
        get => GetValue(IsReadonlyProperty);
        set => SetValue(IsReadonlyProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="SupportedModalities"/> property.
    /// </summary>
    public static readonly StyledProperty<Modalities> SupportedModalitiesProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, Modalities>(nameof(SupportedModalities));

    /// <summary>
    /// Gets or sets the modalities supported by this control. This can be used to determine which
    /// types of content (for example text, images, or videos) can be displayed or interacted with.
    /// </summary>
    public Modalities SupportedModalities
    {
        get => GetValue(SupportedModalitiesProperty);
        set => SetValue(SupportedModalitiesProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="CopyMessageCommand"/> property, which is a command that can be used to copy a chat message.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessage>?> CopyMessageCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessage>?>(nameof(CopyMessageCommand));

    /// <summary>
    /// Gets or sets the command that can be used to copy a chat message. This command can be bound to UI elements to provide functionality for copying messages.
    /// </summary>
    public IRelayCommand<ChatMessage>? CopyMessageCommand
    {
        get => GetValue(CopyMessageCommandProperty);
        set => SetValue(CopyMessageCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="EditMessageNodeCommand"/> property, which is a command that can be used to edit a chat message node.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessageNode>?> EditMessageNodeCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessageNode>?>(nameof(EditMessageNodeCommand));

    /// <summary>
    /// Gets or sets the command that can be used to edit a chat message node. This command can be bound to UI elements to provide functionality for editing message nodes.
    /// </summary>
    public IRelayCommand<ChatMessageNode>? EditMessageNodeCommand
    {
        get => GetValue(EditMessageNodeCommandProperty);
        set => SetValue(EditMessageNodeCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="RetryMessageNodeCommand"/> property, which is a command that can be used to retry a chat message node.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessageNode>?> RetryMessageNodeCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessageNode>?>(nameof(RetryMessageNodeCommand));

    /// <summary>
    /// Gets or sets the command that can be used to retry a chat message node. This command can be bound to UI elements to provide functionality for retrying message nodes.
    /// </summary>
    public IRelayCommand<ChatMessageNode>? RetryMessageNodeCommand
    {
        get => GetValue(RetryMessageNodeCommandProperty);
        set => SetValue(RetryMessageNodeCommandProperty, value);
    }

    /// <summary>
    /// Defines the <see cref="ContinueMessageNodeCommand"/> property, which is a command that can be used to continue a chat message node.
    /// </summary>
    public static readonly StyledProperty<IRelayCommand<ChatMessageNode>?> ContinueMessageNodeCommandProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, IRelayCommand<ChatMessageNode>?>(nameof(ContinueMessageNodeCommand));

    /// <summary>
    /// Gets or sets the command that can be used to continue a chat message node. This command can be bound to UI elements to provide functionality for continuing message nodes.
    /// </summary>
    public IRelayCommand<ChatMessageNode>? ContinueMessageNodeCommand
    {
        get => GetValue(ContinueMessageNodeCommandProperty);
        set => SetValue(ContinueMessageNodeCommandProperty, value);
    }

    public static readonly StyledProperty<ChatMessageNode?> EditingMessageNodeProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, ChatMessageNode?>(nameof(EditingMessageNode));

    public ChatMessageNode? EditingMessageNode
    {
        get => GetValue(EditingMessageNodeProperty);
        set => SetValue(EditingMessageNodeProperty, value);
    }

    public static readonly StyledProperty<bool> ShowStatisticsProperty =
        AvaloniaProperty.Register<ChatMessageItemsControl, bool>(nameof(ShowStatistics));

    public bool ShowStatistics
    {
        get => GetValue(ShowStatisticsProperty);
        set => SetValue(ShowStatisticsProperty, value);
    }

    /// <summary>
    /// Defines the user turn at the center of the current message viewport.
    /// </summary>
    public static readonly DirectProperty<ChatMessageItemsControl, ChatMessageNode?> ReadingTurnNodeProperty =
        AvaloniaProperty.RegisterDirect<ChatMessageItemsControl, ChatMessageNode?>(nameof(ReadingTurnNode), control => control.ReadingTurnNode);

    /// <summary>
    /// Gets the user turn at the center of the current message viewport.
    /// </summary>
    public ChatMessageNode? ReadingTurnNode
    {
        get => _readingTurnNode;
        private set => SetAndRaise(ReadingTurnNodeProperty, ref _readingTurnNode, value);
    }

    private ChatMessageNode? _readingTurnNode;
    private int _turnNavigationRevision;
    private ScrollViewer? _observedScrollViewer;
    private PendingViewportAnchor? _pendingViewportAnchor;
    private bool _edgeLoadingEnabled;
    private bool _edgeCheckQueued;
    private bool _isTailPinned;
    private bool _scrollToEndQueued;
    private int _verticalScrollDirection;

    private const double ScrollStateTolerance = 0.5;

    static ChatMessageItemsControl()
    {
        ChatContextProperty.Changed.AddClassHandler<ChatMessageItemsControl>((control, _) => control.ResetItemsSource());
    }

    public ChatMessageItemsControl()
    {
        LayoutUpdated += HandleLayoutUpdated;
    }

    private void ResetItemsSource()
    {
        _turnNavigationRevision++;
        ReadingTurnNode = null;
        // ChatContext owns the windowed projection companion. Detaching a view releases only its
        // binding; another view receives the same current window and its stable row instances.
        _pendingViewportAnchor = null;
        SetCurrentValue(ItemsSourceProperty, ChatContext?.Presentation.Rows);
        _edgeLoadingEnabled = false;
        _isTailPinned = ChatContext?.Presentation.IsAtLatest == true;
        _verticalScrollDirection = 0;
        Dispatcher.Post(
            () =>
            {
                if (VisualRoot is null) return;
                _edgeLoadingEnabled = true;
                ReconnectScrollViewer();
                RequestScrollToEnd();
                RequestEdgeCheck();
            },
            DispatcherPriority.Background);
    }

    /// <summary>
    /// Shrinks the materialized history around the turns currently intersecting the viewport. The
    /// visual anchor is retained locally because pixel positioning belongs to this view, not to the
    /// presentation projection.
    /// </summary>
    public void CompactCurrentViewport()
    {
        if (_pendingViewportAnchor is not null ||
            ChatContext is not { } context ||
            ItemsPanelRoot is not VariableHeightVirtualizingStackPanel panel ||
            !panel.TryGetViewportSnapshot(out var snapshot))
        {
            return;
        }

        var rows = context.Presentation.Rows;
        if ((uint)snapshot.FirstIndex >= (uint)rows.Count ||
            (uint)snapshot.LastIndex >= (uint)rows.Count ||
            (uint)snapshot.AnchorIndex >= (uint)rows.Count)
        {
            return;
        }

        var wasEdgeLoadingEnabled = _edgeLoadingEnabled;
        _edgeLoadingEnabled = false;
        _pendingViewportAnchor = new PendingViewportAnchor(rows[snapshot.AnchorIndex], snapshot.OffsetWithinAnchor);

        if (context.Presentation.CompactAround(rows[snapshot.FirstIndex], rows[snapshot.LastIndex]))
        {
            return;
        }

        _pendingViewportAnchor = null;
        _edgeLoadingEnabled = wasEdgeLoadingEnabled;
    }

    private void HandleLayoutUpdated(object? sender, EventArgs e)
    {
        UpdateReadingTurn();
        if (_pendingViewportAnchor is not { } anchor ||
            !IsEffectivelyVisible ||
            ItemsPanelRoot is not VariableHeightVirtualizingStackPanel panel)
        {
            return;
        }

        var index = ItemsView.IndexOf(anchor.Row);
        if (index < 0 || !panel.CenterViewportAnchor(index, anchor.OffsetWithinItem))
            return;

        _pendingViewportAnchor = null;
        _edgeLoadingEnabled = VisualRoot is not null;

        if (_observedScrollViewer is { } scrollViewer && ChatContext?.Presentation is { } presentation)
        {
            var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
            _isTailPinned = presentation.IsAtLatest && scrollViewer.Offset.Y >= maximumOffset - ScrollStateTolerance;
            _verticalScrollDirection = _isTailPinned ? 1 : 0;
        }

        RequestEdgeCheck();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReconnectScrollViewer();
        _edgeLoadingEnabled = true;
        RequestScrollToEnd();
        RequestEdgeCheck();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _turnNavigationRevision++;
        if (_observedScrollViewer is not null)
        {
            _observedScrollViewer.ScrollChanged -= HandleScrollViewerScrollChanged;
            _observedScrollViewer = null;
        }

        _edgeLoadingEnabled = false;
        _edgeCheckQueued = false;
        base.OnDetachedFromVisualTree(e);
    }

    private void ReconnectScrollViewer()
    {
        var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (ReferenceEquals(scrollViewer, _observedScrollViewer)) return;

        if (_observedScrollViewer is not null)
            _observedScrollViewer.ScrollChanged -= HandleScrollViewerScrollChanged;

        _observedScrollViewer = scrollViewer;
        if (scrollViewer is not null)
            scrollViewer.ScrollChanged += HandleScrollViewerScrollChanged;
    }

    private void HandleScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _observedScrollViewer) ||
            sender is not ScrollViewer scrollViewer ||
            !ReferenceEquals(e.Source, scrollViewer))
        {
            return;
        }

        if (_pendingViewportAnchor is not null)
            return;

        var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isAtEnd = scrollViewer.Offset.Y >= maximumOffset - ScrollStateTolerance;
        var extentChanged = Math.Abs(e.ExtentDelta.Y) > ScrollStateTolerance;
        var viewportChanged = Math.Abs(e.ViewportDelta.Y) > ScrollStateTolerance;
        var offsetChanged = Math.Abs(e.OffsetDelta.Y) > ScrollStateTolerance;

        if (e.OffsetDelta.Y < -ScrollStateTolerance && !isAtEnd)
        {
            // An upward offset change is user navigation even when realization changes the extent
            // in the same layout pass. It must immediately release tail following.
            _verticalScrollDirection = -1;
            _isTailPinned = false;
        }
        else if (!extentChanged && !viewportChanged && offsetChanged)
        {
            // Only a pure offset change can establish tail intent. Prepending a batch changes both
            // extent and offset while the panel preserves the visible anchor.
            _verticalScrollDirection = Math.Sign(e.OffsetDelta.Y);
            _isTailPinned = isAtEnd;
        }

        if (ChatContext?.Presentation.IsAtLatest != true)
            _isTailPinned = false;
        else if (_isTailPinned && (extentChanged || viewportChanged) && !isAtEnd)
            RequestScrollToEnd();

        RequestEdgeCheck();
    }

    private void UpdateReadingTurn()
    {
        if (!IsEffectivelyVisible || ChatContext is not { } context ||
            ItemsPanelRoot is not VariableHeightVirtualizingStackPanel panel ||
            !panel.TryGetViewportSnapshot(out var snapshot) ||
            (uint)snapshot.AnchorIndex >= (uint)context.Presentation.Rows.Count) return;
        ReadingTurnNode = context.Presentation.GetUserTurnNode(context.Presentation.Rows[snapshot.AnchorIndex]);
    }

    /// <summary>
    /// Reveals a user turn without tail following or edge prefetch competing with the explicit jump.
    /// A newer request, context switch, or detach invalidates any older asynchronous completion.
    /// </summary>
    public async Task RevealTurnAsync(ChatMessageNode node)
    {
        if (ChatContext is not { } context || VisualRoot is null) return;
        var revision = ++_turnNavigationRevision;
        _isTailPinned = false;
        _edgeLoadingEnabled = false;
        _pendingViewportAnchor = null;
        try
        {
            var row = await context.Presentation.RevealAsync(node, null);
            if (row is null || revision != _turnNavigationRevision || !ReferenceEquals(ChatContext, context) || VisualRoot is null) return;
            ScrollIntoView(row);
            TopLevel.GetTopLevel(this)?.UpdateLayout();
            if (_observedScrollViewer is { } scrollViewer && ContainerFromIndex(ItemsView.IndexOf(row)) is { } container &&
                container.TranslatePoint(default, scrollViewer) is { } point)
            {
                var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Clamp(scrollViewer.Offset.Y + point.Y - 12, 0, maximum));
            }
            _isTailPinned = false;
            _verticalScrollDirection = 0;
            UpdateReadingTurn();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to navigate to a chat turn.");
        }
        finally
        {
            if (revision == _turnNavigationRevision) _edgeLoadingEnabled = VisualRoot is not null;
        }
    }

    private void RequestScrollToEnd()
    {
        if (_scrollToEndQueued) return;

        _scrollToEndQueued = true;
        Dispatcher.Post(
            () =>
            {
                _scrollToEndQueued = false;
                if (!_isTailPinned ||
                    _pendingViewportAnchor is not null ||
                    ChatContext?.Presentation.IsAtLatest != true ||
                    _observedScrollViewer is not { } scrollViewer ||
                    VisualRoot is null)
                {
                    return;
                }

                scrollViewer.Offset = new Vector(scrollViewer.Offset.X, double.PositiveInfinity);
            },
            DispatcherPriority.Loaded);
    }

    private void RequestEdgeCheck()
    {
        if (!_edgeLoadingEnabled || _edgeCheckQueued || _observedScrollViewer is null) return;

        _edgeCheckQueued = true;
        Dispatcher.Post(CheckWindowEdges, DispatcherPriority.Background);
    }

    private void CheckWindowEdges()
    {
        _edgeCheckQueued = false;
        if (!_edgeLoadingEnabled ||
            _observedScrollViewer is not { Viewport.Height: > 0 } scrollViewer ||
            ChatContext?.Presentation is not { IsWindowOperationActive: false } presentation)
        {
            return;
        }

        var threshold = Math.Max(240, scrollViewer.Viewport.Height);
        var maximumOffset = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var cannotScroll = maximumOffset <= 0.5;
        var nearStart = scrollViewer.Offset.Y <= threshold;
        var nearEnd = maximumOffset - scrollViewer.Offset.Y <= threshold;

        Task<bool>? load = null;
        var loadDirection = 0;
        if (nearStart && presentation.HasEarlierTurns && (cannotScroll || !nearEnd || _verticalScrollDirection <= 0))
        {
            load = presentation.LoadEarlierAsync();
            loadDirection = -1;
        }
        else if (nearEnd && presentation.HasLaterTurns)
        {
            load = presentation.LoadLaterAsync();
            loadDirection = 1;
        }

        if (load is not null)
            ObserveEdgeLoadAsync(presentation, load, loadDirection).Detach();
    }

    private async Task ObserveEdgeLoadAsync(ChatPresentation presentation, Task<bool> loadTask, int loadDirection)
    {
        try
        {
            await loadTask;
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to materialize a chat presentation batch.");
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!ReferenceEquals(ChatContext?.Presentation, presentation)) return;

                // Avalonia's prepend anchor correction changes Offset in the opposite direction
                // from the user's navigation intent. Restore the logical direction before deciding
                // whether the same edge still needs another batch.
                _verticalScrollDirection = loadDirection;
                RequestEdgeCheck();
            });
        }
    }

    /// <summary>
    /// Replaces a history-centered window with the bounded latest window before moving to its end.
    /// </summary>
    [RelayCommand]
    private async Task ShowLatestAsync()
    {
        if (ChatContext?.Presentation is not { } presentation) return;
        if (!await presentation.ShowLatestAsync()) return;

        if (!ReferenceEquals(ChatContext?.Presentation, presentation)) return;

        _isTailPinned = true;
        _verticalScrollDirection = 1;
        RequestScrollToEnd();
    }

    /// <summary>
    /// Opens a URL surfaced by either a lightweight activity preview or a detailed plugin display
    /// block. Both presentations belong to this chat root, so the command is intentionally hosted
    /// here instead of being duplicated by individual presenters.
    /// </summary>
    [RelayCommand]
    private static Task<bool> OpenUrlAsync(object? value)
    {
        var uri = value switch
        {
            Uri u => u,
            LinkClickedEventArgs e => e.HRef,
            _ when Uri.TryCreate(value?.ToString(), UriKind.Absolute, out var u) => u,
            _ => null,
        };

        // TODO: file schema and more with safety check.
        return uri is not { Scheme: "http" or "https" } ? Task.FromResult(false) : App.Launcher.LaunchUriAsync(uri);
    }

    /// <summary>
    /// Opens a subagent conversation in its independent dialog. A nested subagent view creates its
    /// own <see cref="ChatMessageItemsControl"/>, so recursive subagent conversations retain the
    /// same command boundary without coupling either display-block presenter to dialog services.
    /// </summary>
    [RelayCommand]
    private static void OpenSubagent(ChatPluginSubagentDisplayBlock block)
    {
        DialogManager
            .CreateCustomDialog(
                new ChatSubagentView
                {
                    ChatContext = block.ChatContext
                })
            .Dismissible()
            .Show();
    }

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is ChatPresentationRow)
        {
            recycleKey = typeof(ChatPresentationRowPresenter);
            return true;
        }

        // The projection is the only supported source for this control.  Let the base
        // ItemsControl handle an unexpected value rather than reviving the old raw-node
        // compatibility path.
        return base.NeedsContainerOverride(item, index, out recycleKey);
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        item is ChatPresentationRow ? new ChatPresentationRowPresenter() : base.CreateContainerForItemOverride(item, index, recycleKey);

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
    {
        base.PrepareContainerForItemOverride(container, item, index);

        if (container is ChatPresentationRowPresenter presentationControl && item is ChatPresentationRow row)
        {
            presentationControl.SetRow(row, row.TryMarkPresented());
        }
    }

    protected override void ClearContainerForItemOverride(Control container)
    {
        switch (container)
        {
            case ChatPresentationRowPresenter presentationControl:
            {
                presentationControl.ClearRow();
                break;
            }
        }

        base.ClearContainerForItemOverride(container);
    }

    private readonly record struct PendingViewportAnchor(
        ChatPresentationRow Row,
        double OffsetWithinItem
    );
}
