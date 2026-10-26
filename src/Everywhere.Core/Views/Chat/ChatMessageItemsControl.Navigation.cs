using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using Serilog;

namespace Everywhere.Views;

public sealed partial class ChatMessageItemsControl
{
    private bool CanUseViewport => _isHostActive && VisualRoot is not null && IsEffectivelyVisible;

    private bool CanLoadEdges => CanUseViewport && _navigationTarget is null && _windowCancellation is null;

    private ScrollViewer? _observedScrollViewer;
    private ScrollBar? _observedScrollBar;
    private double _lastScrollOffset;
    private ChatTextSearchViewModel? _subscribedTextSearch;
    private CancellationTokenSource? _windowCancellation;
    private NavigationTarget? _navigationTarget;
    private ChatPresentationRow? _navigationRow;
    private ReadingAnchor? _readingAnchor;
    private int _navigationRevision;
    private int _verticalScrollDirection;
    private bool _isHostActive = true;
    private bool _isTailPinned;
    private bool _isNavigationPrepared;
    private bool _isPositioning;
    private bool _edgeCheckQueued;

    private const double ScrollStateTolerance = 0.5;

    private void ResetItemsSource()
    {
        EndNavigation();
        _readingAnchor = null;
        ReadingTurnNode = null;
        // The context owns the projection; view attachment never owns its disposal.
        SetCurrentValue(ItemsSourceProperty, ChatContext?.Presentation.Rows);
        _isTailPinned = ChatContext?.Presentation.IsAtLatest == true;
        _verticalScrollDirection = 0;
        ResumeReading();
    }

    /// <summary>
    /// Suspends view-owned loading and positioning while the host is hidden or minimized.
    /// Logical navigation and the last visible reading anchor survive until the host resumes.
    /// </summary>
    public void SetViewportActive(bool isActive)
    {
        if (_isHostActive == isActive)
        {
            return;
        }

        _isHostActive = isActive;
        if (!isActive)
        {
            SuspendNavigation();
        }
        else
        {
            ResumeReading();
        }
    }

    /// <summary>
    /// Trims a suspended view using row identities captured during its last valid visible layout.
    /// This does not load bodies, parse new content, or reinterpret hidden viewport coordinates.
    /// </summary>
    public void CompactCurrentViewport()
    {
        if (_isHostActive || ChatContext is not { } context)
        {
            return;
        }

        var presentation = context.Presentation;
        if (_isTailPinned || _navigationTarget?.Kind == NavigationKind.Latest)
        {
            if (presentation.Rows.LastOrDefault() is { } last)
            {
                presentation.CompactAround(last, last);
            }
        }
        else if (_readingAnchor is { } anchor)
        {
            presentation.CompactAround(anchor.FirstRow, anchor.LastRow);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ReconnectScrollViewer();
        ReconnectTextSearch();
        TextSearchSurfaceRegistry.SurfaceChanged += HandleSearchSurfaceChanged;
        ResumeReading();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SuspendNavigation();
        DisconnectScrollViewer();
        DisconnectTextSearch();
        TextSearchSurfaceRegistry.SurfaceChanged -= HandleSearchSurfaceChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void ResumeReading()
    {
        // Reserve the restore target before the first visible layout can replace the saved anchor
        // with geometry from the compacted window or the new window size.
        if (_navigationTarget is null)
        {
            if (_isTailPinned)
            {
                _navigationTarget = new NavigationTarget(NavigationKind.Latest);
            }
            else if (_readingAnchor is { } anchor)
            {
                _navigationTarget = new NavigationTarget(NavigationKind.Restore, anchor.UserNode, null, anchor);
            }
        }
        if (!CanUseViewport || _windowCancellation is not null)
        {
            return;
        }

        // Result publication resumes a pending search; layout must not restart it while matching.
        if (_navigationTarget?.Kind == NavigationKind.Search && TextSearch is { IsOpen: true, IsBusy: true })
        {
            return;
        }

        ReconnectScrollViewer();
        if (_navigationTarget is { } target)
        {
            NavigateAsync(target).Detach();
        }
        else
        {
            CaptureReadingAnchor();
            RequestEdgeCheck();
        }
    }

    private void SuspendNavigation()
    {
        _navigationRevision++;
        _navigationRow = null;
        _isNavigationPrepared = false;
        _edgeCheckQueued = false;
        var cancellation = _windowCancellation;
        _windowCancellation = null;
        // Each async owner disposes its source in finally. Cancel only its wait, not shared parsing.
        cancellation?.Cancel();
    }

    private void EndNavigation()
    {
        SuspendNavigation();
        _navigationTarget = null;
        _verticalScrollDirection = 0;
        RequestEdgeCheck();
    }

    /// <summary>
    /// Reveals a bounded window around a user turn and aligns its message near the viewport top.
    /// </summary>
    public Task RevealTurnAsync(ChatMessageNode node)
    {
        return NavigateAsync(new NavigationTarget(NavigationKind.Turn, node));
    }

    /// <summary>
    /// Publishes a bounded latest window, then establishes tail following after positioning.
    /// </summary>
    [RelayCommand]
    private Task ShowLatestAsync() => NavigateAsync(new NavigationTarget(NavigationKind.Latest));

    private async Task NavigateAsync(NavigationTarget target)
    {
        SuspendNavigation();
        _navigationTarget = target;
        _isTailPinned = false;
        _verticalScrollDirection = 0;
        if (!CanUseViewport || ChatContext is not { } context)
        {
            return;
        }

        if (target.Kind == NavigationKind.Search)
        {
            if (TextSearch is { IsOpen: true, IsBusy: true })
            {
                // Reserve the intent now. Publishing results may finish it, but must not
                // recreate it after scrolling or another navigation has superseded it.
                return;
            }
            if (TextSearch is not { IsOpen: true } search || search.GetCurrentMatch() is not { } match)
            {
                EndNavigation();
                return;
            }
            target = new NavigationTarget(NavigationKind.Search, match.Node, match);
            _navigationTarget = target;
        }

        var revision = _navigationRevision;
        using var cancellation = new CancellationTokenSource();
        _windowCancellation = cancellation;
        try
        {
            var presentation = context.Presentation;
            var row = default(ChatPresentationRow);
            if (target.Kind == NavigationKind.Latest)
            {
                if (!await presentation.ShowLatestAsync(cancellation.Token))
                {
                    return;
                }
            }
            else if (target is { Kind: NavigationKind.Restore, Anchor: { } anchor } &&
                     !presentation.IsWindowOperationActive && presentation.Rows.Contains(anchor.Row))
            {
                row = anchor.Row;
            }
            else if (target.Node is { } node)
            {
                row = await presentation.RevealAsync(node, target.Match?.Span, cancellation.Token);
                if (target.Kind == NavigationKind.Restore && row is not null)
                {
                    // The old output disappeared; the surviving user turn is the explicit fallback.
                    target = new NavigationTarget(NavigationKind.Turn, node);
                }
            }

            if (revision != _navigationRevision || !CanUseViewport || !ReferenceEquals(ChatContext, context))
            {
                return;
            }

            if (target.Kind != NavigationKind.Latest && row is null)
            {
                EndNavigation();
                if (target.Kind == NavigationKind.Restore)
                {
                    await ShowLatestAsync();
                }
                return;
            }

            _navigationTarget = target;
            _navigationRow = row;
            _isNavigationPrepared = true;
            if (row is not null)
            {
                ScrollIntoView(row);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to navigate the chat viewport.");
            if (revision == _navigationRevision)
            {
                EndNavigation();
            }
        }
        finally
        {
            if (ReferenceEquals(_windowCancellation, cancellation))
            {
                _windowCancellation = null;
                if (!_isNavigationPrepared)
                {
                    EndNavigation();
                }
                RequestPosition();
                RequestEdgeCheck();
            }
        }
    }

    private void HandleLayoutUpdated(object? sender, EventArgs e)
    {
        if (!CanUseViewport)
        {
            return;
        }

        if (_observedScrollBar is null && _observedScrollViewer is { } scrollViewer)
        {
            _observedScrollBar = scrollViewer.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(bar => bar.Orientation == Orientation.Vertical && IsOuterScrollInput(bar));
            if (_observedScrollBar is not null)
            {
                _lastScrollOffset = scrollViewer.Offset.Y;
                _observedScrollBar.Scroll += HandleScrollBar;
            }
        }

        // Attachment can precede effective visibility. The first usable layout resumes the
        // existing intent; prepared navigation is positioned here, before rendering.
        if (_navigationTarget is not null && !_isNavigationPrepared && _windowCancellation is null)
        {
            ResumeReading();
        }

        if (_navigationTarget is not null || _isTailPinned)
        {
            PositionViewport();
        }

        // Tail following and reading position are independent. Pending navigation keeps its anchor.
        CaptureReadingAnchor();
    }

    private void RequestPosition()
    {
        if (!CanUseViewport || _windowCancellation is not null ||
            _navigationTarget is not null && !_isNavigationPrepared)
        {
            return;
        }

        // Layout coalesces requests and resolves the current navigation, not a captured callback.
        InvalidateArrange();
    }

    private void PositionViewport()
    {
        if (!CanUseViewport || _windowCancellation is not null || _navigationTarget is not null && !_isNavigationPrepared ||
            _observedScrollViewer is not { Viewport.Height: > 0 } scrollViewer || ChatContext is not { } context)
        {
            return;
        }

        var target = _navigationTarget;
        if (target?.Node is { } node && !context.Data.Items.Contains(node))
        {
            EndNavigation();
            if (target.Kind == NavigationKind.Restore)
            {
                ShowLatestAsync().Detach();
            }
            return;
        }
        _isPositioning = true;
        try
        {
            if (target?.Kind == NavigationKind.Latest || target is null && _isTailPinned)
            {
                if (!context.Presentation.IsAtLatest)
                {
                    return;
                }
                scrollViewer.ScrollToEnd();
                _isTailPinned = true;
            }
            else if (target is not null && _navigationRow is { } row)
            {
                var index = ItemsView.IndexOf(row);
                if (index < 0)
                {
                    // A branch change can remove the row after preheating but before layout.
                    EndNavigation();
                    if (target.Kind == NavigationKind.Restore)
                    {
                        NavigateAsync(target).Detach();
                    }
                    return;
                }

                double targetOffset;
                if (target.Match is { } match)
                {
                    // Realization can replace the offscreen text projection and rematch it.
                    // A pending rematch is not evidence that this selected result disappeared.
                    if (TextSearch is { IsOpen: true, IsBusy: true })
                    {
                        return;
                    }
                    if (!IsCurrentSearchMatch(match))
                    {
                        EndNavigation();
                        return;
                    }
                    if (!TextSearchSurfaceRegistry.TryGet(row, out var surface) ||
                        !surface.TryGetMatchCenter(match.LocalIndex, scrollViewer, out var center))
                    {
                        return;
                    }
                    targetOffset = scrollViewer.Offset.Y + center.Y - scrollViewer.Viewport.Height / 2;
                }
                else if (target.Anchor is { } anchor && ItemsPanelRoot is VariableHeightVirtualizingStackPanel panel)
                {
                    if (!panel.CenterViewportAnchor(index, anchor.OffsetWithinItem))
                    {
                        return;
                    }
                    targetOffset = scrollViewer.Offset.Y;
                }
                else if (ContainerFromIndex(index) is { } container && container.TranslatePoint(default, scrollViewer) is { } point)
                {
                    targetOffset = scrollViewer.Offset.Y + point.Y - 12;
                }
                else
                {
                    return;
                }

                var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
                scrollViewer.Offset = scrollViewer.Offset.WithY(Math.Clamp(targetOffset, 0, maximum));
            }
            else
            {
                return;
            }

            if (target is not null)
            {
                EndNavigation();
            }
        }
        finally
        {
            _isPositioning = false;
        }
    }

    private void CaptureReadingAnchor()
    {
        if (_isPositioning || _navigationTarget is not null || ChatContext is not { } context ||
            ItemsPanelRoot is not VariableHeightVirtualizingStackPanel panel || !panel.TryGetViewportSnapshot(out var snapshot))
        {
            return;
        }

        var rows = context.Presentation.Rows;
        if ((uint)snapshot.FirstIndex >= (uint)rows.Count || (uint)snapshot.LastIndex >= (uint)rows.Count ||
            (uint)snapshot.AnchorIndex >= (uint)rows.Count)
        {
            return;
        }

        var row = rows[snapshot.AnchorIndex];
        ReadingTurnNode = context.Presentation.GetUserTurnNode(row);
        _readingAnchor = new ReadingAnchor(rows[snapshot.FirstIndex], rows[snapshot.LastIndex], row,
            snapshot.OffsetWithinAnchor, ReadingTurnNode);
    }

    private void ReconnectTextSearch()
    {
        HandleSearchMatchChanged(this, EventArgs.Empty);
        DisconnectTextSearch();
        if (VisualRoot is null || TextSearch is not { } coordinator)
        {
            return;
        }
        _subscribedTextSearch = coordinator;
        coordinator.NavigationRequested += HandleSearchNavigationRequested;
        coordinator.CurrentMatchChanged += HandleSearchMatchChanged;
        coordinator.PropertyChanged += HandleSearchPropertyChanged;
    }

    private void DisconnectTextSearch()
    {
        if (_subscribedTextSearch is not { } coordinator)
        {
            return;
        }
        coordinator.NavigationRequested -= HandleSearchNavigationRequested;
        coordinator.CurrentMatchChanged -= HandleSearchMatchChanged;
        coordinator.PropertyChanged -= HandleSearchPropertyChanged;
        _subscribedTextSearch = null;
    }

    private bool IsCurrentSearchMatch(ChatTextSearchViewModel.ChatTextSearchMatch match)
    {
        return TextSearch is { IsOpen: true } coordinator && coordinator.GetCurrentMatch() is { } current && current.Equals(match);
    }

    private void HandleSearchNavigationRequested(object? sender, EventArgs e)
    {
        NavigateAsync(new NavigationTarget(NavigationKind.Search)).Detach();
    }

    private void HandleSearchMatchChanged(object? sender, EventArgs e)
    {
        if (_navigationTarget?.Kind != NavigationKind.Search || TextSearch is { IsOpen: true, IsBusy: true })
        {
            return;
        }
        if (TextSearch is not { IsOpen: true } search || search.GetCurrentMatch() is not { } match)
        {
            EndNavigation();
        }
        else if (_navigationTarget.Match != match)
        {
            NavigateAsync(new NavigationTarget(NavigationKind.Search, match.Node, match)).Detach();
        }
        else
        {
            RequestPosition();
        }
    }

    private void HandleSearchPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatTextSearchViewModel.IsOpen))
        {
            HandleSearchMatchChanged(sender, EventArgs.Empty);
        }
    }

    private void HandleSearchSurfaceChanged(ChatPresentationRow row)
    {
        if (_navigationTarget?.Kind == NavigationKind.Search && ReferenceEquals(_navigationRow, row))
        {
            RequestPosition();
        }
    }

    private void ReconnectScrollViewer()
    {
        var scrollViewer = this.FindAncestorOfType<ScrollViewer>();
        if (ReferenceEquals(scrollViewer, _observedScrollViewer))
        {
            return;
        }
        DisconnectScrollViewer();
        _observedScrollViewer = scrollViewer;
        if (scrollViewer is not null)
        {
            scrollViewer.ScrollChanged += HandleScrollViewerScrollChanged;
            scrollViewer.AddHandler(PointerWheelChangedEvent, HandleScrollWheel, RoutingStrategies.Tunnel);
            scrollViewer.AddHandler(ScrollGestureEvent, HandleScrollGesture, RoutingStrategies.Tunnel);
            scrollViewer.AddHandler(KeyDownEvent, HandleScrollKey, RoutingStrategies.Tunnel);
        }
    }

    private void DisconnectScrollViewer()
    {
        if (_observedScrollViewer is not { } scrollViewer)
        {
            return;
        }
        scrollViewer.ScrollChanged -= HandleScrollViewerScrollChanged;
        scrollViewer.RemoveHandler(PointerWheelChangedEvent, HandleScrollWheel);
        scrollViewer.RemoveHandler(ScrollGestureEvent, HandleScrollGesture);
        scrollViewer.RemoveHandler(KeyDownEvent, HandleScrollKey);
        if (_observedScrollBar is not null)
        {
            _observedScrollBar.Scroll -= HandleScrollBar;
            _observedScrollBar = null;
        }
        _observedScrollViewer = null;
    }

    private bool IsOuterScrollInput(object? source)
    {
        return source is Visual visual && ReferenceEquals(
            visual as ScrollViewer ?? visual.FindAncestorOfType<ScrollViewer>(), _observedScrollViewer);
    }

    private void HandleScrollWheel(object? sender, PointerWheelEventArgs e)
    {
        if (IsOuterScrollInput(e.Source) && e.Delta.Y != 0)
        {
            BeginUserScroll(-Math.Sign(e.Delta.Y));
        }
    }

    private void HandleScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        if (IsOuterScrollInput(e.Source) && e.Delta.Y != 0)
        {
            BeginUserScroll(Math.Sign(e.Delta.Y));
        }
    }

    private void HandleScrollKey(object? sender, KeyEventArgs e)
    {
        if (!ReferenceEquals(e.Source, _observedScrollViewer))
        {
            return;
        }
        var direction = e.Key switch
        {
            Key.Up or Key.PageUp or Key.Home => -1,
            Key.Down or Key.PageDown or Key.End => 1,
            _ => 0
        };
        if (direction != 0)
        {
            BeginUserScroll(direction);
        }
    }

    private void HandleScrollBar(object? sender, ScrollEventArgs e)
    {
        if (ReferenceEquals(sender, _observedScrollBar))
        {
            BeginUserScroll(Math.Sign(e.NewValue - _lastScrollOffset));
            _lastScrollOffset = e.NewValue;
        }
    }

    private void BeginUserScroll(int direction)
    {
        if (!CanUseViewport)
        {
            return;
        }
        // Ordinary repeated scrolling does not cancel an edge prefetch. It does supersede a jump
        // waiting for Markdown or layout, including a search surface that has not registered yet.
        if (_navigationTarget is not null)
        {
            EndNavigation();
        }
        _isTailPinned = false;
        _verticalScrollDirection = direction;
        RequestEdgeCheck();
    }

    private void HandleScrollViewerScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_observedScrollViewer is { } observed)
        {
            _lastScrollOffset = observed.Offset.Y;
        }
        if (!CanUseViewport || _isPositioning || _navigationTarget is not null ||
            sender is not ScrollViewer scrollViewer || !ReferenceEquals(sender, _observedScrollViewer) ||
            !ReferenceEquals(e.Source, scrollViewer))
        {
            return;
        }

        var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var isAtEnd = scrollViewer.Offset.Y >= maximum - ScrollStateTolerance;
        var hasGeometryChanged = Math.Abs(e.ExtentDelta.Y) > ScrollStateTolerance || Math.Abs(e.ViewportDelta.Y) > ScrollStateTolerance;
        if (!hasGeometryChanged && Math.Abs(e.OffsetDelta.Y) > ScrollStateTolerance)
        {
            if (!isAtEnd)
            {
                _isTailPinned = false;
            }
            else if (_verticalScrollDirection > 0 && ChatContext?.Presentation.IsAtLatest == true)
            {
                _isTailPinned = true;
            }
        }
        if (_isTailPinned && hasGeometryChanged && !isAtEnd)
        {
            RequestPosition();
        }
        RequestEdgeCheck();
    }

    private void RequestEdgeCheck()
    {
        if (!CanLoadEdges || _edgeCheckQueued)
        {
            return;
        }
        _edgeCheckQueued = true;
        var revision = _navigationRevision;
        Dispatcher.UIThread.Post(() =>
        {
            if (revision != _navigationRevision)
            {
                return;
            }
            _edgeCheckQueued = false;
            CheckWindowEdges();
        }, DispatcherPriority.Background);
    }

    private void CheckWindowEdges()
    {
        if (!CanLoadEdges || _observedScrollViewer is not { Viewport.Height: > 0 } scrollViewer ||
            ChatContext?.Presentation is not { IsWindowOperationActive: false } presentation)
        {
            return;
        }

        var threshold = Math.Max(240, scrollViewer.Viewport.Height);
        var maximum = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var cannotScroll = maximum <= ScrollStateTolerance;
        var shouldLoadEarlier = presentation.HasEarlierTurns && scrollViewer.Offset.Y <= threshold &&
                                (cannotScroll || _verticalScrollDirection < 0);
        var shouldLoadLater = presentation.HasLaterTurns && maximum - scrollViewer.Offset.Y <= threshold &&
                              (cannotScroll || _verticalScrollDirection > 0);
        if (shouldLoadEarlier || shouldLoadLater)
        {
            LoadEdgeAsync(presentation, shouldLoadEarlier).Detach();
        }
    }

    private async Task LoadEdgeAsync(ChatPresentation presentation, bool shouldLoadEarlier)
    {
        var revision = _navigationRevision;
        using var cancellation = new CancellationTokenSource();
        _windowCancellation = cancellation;
        var hasLoaded = false;
        try
        {
            hasLoaded = shouldLoadEarlier
                ? await presentation.LoadEarlierAsync(cancellation.Token)
                : await presentation.LoadLaterAsync(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to materialize a chat presentation batch.");
        }
        finally
        {
            if (ReferenceEquals(_windowCancellation, cancellation))
            {
                _windowCancellation = null;
            }
        }
        if (hasLoaded && revision == _navigationRevision && CanUseViewport)
        {
            if (_isTailPinned)
            {
                RequestPosition();
            }
            RequestEdgeCheck();
        }
    }

    private enum NavigationKind
    {
        Latest,
        Turn,
        Search,
        Restore
    }

    private sealed record NavigationTarget(
        NavigationKind Kind,
        ChatMessageNode? Node = null,
        ChatTextSearchViewModel.ChatTextSearchMatch? Match = null,
        ReadingAnchor? Anchor = null);

    private sealed record ReadingAnchor(
        ChatPresentationRow FirstRow,
        ChatPresentationRow LastRow,
        ChatPresentationRow Row,
        double OffsetWithinItem,
        ChatMessageNode? UserNode);
}