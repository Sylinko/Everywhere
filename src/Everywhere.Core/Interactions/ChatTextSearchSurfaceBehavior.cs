using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Xaml.Interactivity;
using Everywhere.Utilities;
using Everywhere.Views;
using LiveMarkdown.Avalonia;

namespace Everywhere.Interactions;

/// <summary>
/// Provides the inherited highlight styles used by current-conversation search surfaces.
/// </summary>
public static class ChatTextSearchHighlightStyles
{
    public static TextHighlightStyles Instance { get; } = Create();

    private static TextHighlightStyles Create()
    {
        var styles = new TextHighlightStyles();
        styles.Set(
            MarkdownRenderer.DefaultTextSearchHighlightName,
            new TextHighlightStyle
            {
                Background = new SolidColorBrush(Color.FromArgb(63, 255, 193, 7)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(1, 0),
            });
        styles.Set(
            ChatTextSearchSurfaceBehavior.CurrentHighlightName,
            new TextHighlightStyle
            {
                Background = new SolidColorBrush(Color.FromArgb(192, 255, 152, 0)),
                CornerRadius = new CornerRadius(2),
                Padding = new Thickness(1, 0),
            });
        return styles;
    }
}

/// <summary>
/// Bridges a realized Markdown search surface to the view-model search coordinator. The behavior
/// owns only visual highlighting and registration; global counting remains independent from
/// virtualization in <see cref="ChatTextSearchViewModel"/>.
/// </summary>
public sealed class ChatTextSearchSurfaceBehavior : Behavior<Control>, IChatTextSearchSurface
{
    internal const string CurrentHighlightName = "search-current";

    public static readonly StyledProperty<ChatMessageItemsControl?> HostProperty =
        AvaloniaProperty.Register<ChatTextSearchSurfaceBehavior, ChatMessageItemsControl?>(nameof(Host));

    public static readonly StyledProperty<ChatTextSearchViewModel?> CoordinatorProperty =
        AvaloniaProperty.Register<ChatTextSearchSurfaceBehavior, ChatTextSearchViewModel?>(nameof(Coordinator));

    public static readonly StyledProperty<ChatPresentationRow?> RowProperty =
        AvaloniaProperty.Register<ChatTextSearchSurfaceBehavior, ChatPresentationRow?>(nameof(Row));

    public ChatMessageItemsControl? Host
    {
        get => GetValue(HostProperty);
        set => SetValue(HostProperty, value);
    }

    public ChatTextSearchViewModel? Coordinator
    {
        get => GetValue(CoordinatorProperty);
        set => SetValue(CoordinatorProperty, value);
    }

    public ChatPresentationRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    private IDisposable? _registration;
    private CompositeDisposable? _rendererSubscriptions;
    private ChatTextSearchViewModel? _subscribedCoordinator;
    private TextHighlightMatch? _currentRendererMatch;
    private TextHighlightRange[] _plainMatches = [];

    protected override void OnAttached()
    {
        base.OnAttached();

        switch (AssociatedObject)
        {
            case MarkdownRenderer renderer:
                _rendererSubscriptions =
                [
                    renderer.GetObservable(MarkdownRenderer.RenderedTextProjectionProperty).Subscribe(_ => HandleRenderedTextProjectionChanged()),
                    renderer.GetObservable(MarkdownRenderer.TextSearchMatchesProperty).Subscribe(_ => HandleRendererTextSearchMatchesChanged()),
                ];
                break;
            case MarkdownTextBlock textBlock:
                textBlock.PropertyChanged += HandleTextBlockPropertyChanged;
                break;
        }

        Reconnect();
    }

    protected override void OnDetaching()
    {
        Disconnect();

        switch (AssociatedObject)
        {
            case MarkdownRenderer renderer:
                DisposeHelper.DisposeToDefault(ref _rendererSubscriptions);
                renderer.ClearTextSearch();
                break;
            case MarkdownTextBlock textBlock:
                textBlock.PropertyChanged -= HandleTextBlockPropertyChanged;
                textBlock.Highlights.Remove(MarkdownRenderer.DefaultTextSearchHighlightName);
                break;
        }

        ClearCurrentHighlight();
        base.OnDetaching();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HostProperty || change.Property == CoordinatorProperty || change.Property == RowProperty)
        {
            Reconnect();
        }
    }

    public bool TryGetMatchCenter(int localIndex, Visual relativeTo, out Point center)
    {
        switch (AssociatedObject)
        {
            case MarkdownRenderer renderer when localIndex >= 0 && localIndex < renderer.TextSearchMatches.Count:
                return TryTranslateMatchCenter(renderer.TextSearchMatches[localIndex], relativeTo, out center);
            case MarkdownTextBlock textBlock when localIndex >= 0 && localIndex < _plainMatches.Length:
                return TryTranslateMatchCenter(new TextHighlightMatch(textBlock, _plainMatches[localIndex]), relativeTo, out center);
            default:
                center = default;
                return false;
        }
    }

    private void Reconnect()
    {
        if (AssociatedObject is null) return;

        Disconnect();
        ClearSearchVisuals();
        if (Coordinator is not { } coordinator || Host is not { } host || Row is not { } row) return;

        _subscribedCoordinator = coordinator;
        coordinator.VisualStateChanged += HandleVisualStateChanged;
        coordinator.CurrentMatchChanged += HandleCurrentMatchChanged;
        _registration = host.TextSearchSurfaceRegistry.Register(row, this);
        PublishRenderedProjection();
        UpdateVisualState();
    }

    private void Disconnect()
    {
        if (_subscribedCoordinator is not null)
        {
            _subscribedCoordinator.VisualStateChanged -= HandleVisualStateChanged;
            _subscribedCoordinator.CurrentMatchChanged -= HandleCurrentMatchChanged;
            _subscribedCoordinator = null;
        }

        _registration?.Dispose();
        _registration = null;
    }

    private void HandleVisualStateChanged(object? sender, EventArgs e) => UpdateVisualState();

    private void HandleCurrentMatchChanged(object? sender, EventArgs e)
    {
        UpdateCurrentHighlight();
        Host?.TextSearchSurfaceRegistry.NotifyChanged(Row);
    }

    private void HandleRenderedTextProjectionChanged()
    {
        PublishRenderedProjection();
        Host?.TextSearchSurfaceRegistry.NotifyChanged(Row);
    }

    private void HandleRendererTextSearchMatchesChanged()
    {
        UpdateCurrentHighlight();
        Host?.TextSearchSurfaceRegistry.NotifyChanged(Row);
    }

    private void HandleTextBlockPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextBlock.TextProperty)
        {
            UpdateVisualState();
        }
    }

    private void PublishRenderedProjection()
    {
        if (Coordinator is not { } coordinator ||
            Row is not AssistantTextOutputPresentationRow row ||
            AssociatedObject is not MarkdownRenderer { RenderedTextProjection: { } projection })
        {
            return;
        }

        coordinator.AcceptRenderedProjection(row, row.TextSpan.ContentMarkdownBuilder, projection);
    }

    private void UpdateVisualState()
    {
        ClearCurrentHighlight();
        _plainMatches = [];

        var pattern = Coordinator is { } coordinator && Row is { } row && coordinator.IsRowIncluded(row) ? coordinator.ActivePattern : null;
        switch (AssociatedObject)
        {
            case MarkdownRenderer renderer:
                if (pattern is null) renderer.ClearTextSearch();
                else renderer.ApplyTextSearch(pattern);
                break;
            case MarkdownTextBlock textBlock:
                if (pattern is null)
                {
                    textBlock.Highlights.Remove(MarkdownRenderer.DefaultTextSearchHighlightName);
                }
                else
                {
                    _plainMatches = [.. pattern.FindRanges(textBlock.LayoutText)];
                    if (_plainMatches.Length == 0)
                    {
                        textBlock.Highlights.Remove(MarkdownRenderer.DefaultTextSearchHighlightName);
                    }
                    else
                    {
                        textBlock.Highlights.Set(MarkdownRenderer.DefaultTextSearchHighlightName, _plainMatches);
                    }
                }
                break;
        }

        UpdateCurrentHighlight();
        Host?.TextSearchSurfaceRegistry.NotifyChanged(Row);
    }

    private void UpdateCurrentHighlight()
    {
        ClearCurrentHighlight();
        if (Coordinator is not { } coordinator || Row is not { } row) return;

        var localIndex = coordinator.GetCurrentLocalIndex(row);
        switch (AssociatedObject)
        {
            case MarkdownRenderer renderer when localIndex >= 0 && localIndex < renderer.TextSearchMatches.Count:
            {
                var match = renderer.TextSearchMatches[localIndex];
                match.Block.Highlights.Set(CurrentHighlightName, [match.Range], priority: 1);
                _currentRendererMatch = match;
                break;
            }
            case MarkdownTextBlock textBlock when localIndex >= 0 && localIndex < _plainMatches.Length:
                textBlock.Highlights.Set(CurrentHighlightName, [_plainMatches[localIndex]], priority: 1);
                break;
        }
    }

    private void ClearCurrentHighlight()
    {
        if (_currentRendererMatch is { } match)
        {
            match.Block.Highlights.Remove(CurrentHighlightName);
            _currentRendererMatch = null;
        }

        if (AssociatedObject is MarkdownTextBlock textBlock)
        {
            textBlock.Highlights.Remove(CurrentHighlightName);
        }
    }

    private void ClearSearchVisuals()
    {
        ClearCurrentHighlight();
        _plainMatches = [];

        switch (AssociatedObject)
        {
            case MarkdownRenderer renderer:
                renderer.ClearTextSearch();
                break;
            case MarkdownTextBlock textBlock:
                textBlock.Highlights.Remove(MarkdownRenderer.DefaultTextSearchHighlightName);
                break;
        }
    }

    private static bool TryTranslateMatchCenter(TextHighlightMatch match, Visual relativeTo, out Point center)
    {
        var bounds = match.Block.GetTextRangeBoundsInControl(match.Range.Start, match.Range.Length);
        if (bounds.Count == 0)
        {
            center = default;
            return false;
        }

        var target = bounds[0];
        for (var i = 1; i < bounds.Count; i++)
        {
            target = target.Union(bounds[i]);
        }

        var translated = match.Block.TranslatePoint(target.Center, relativeTo);
        center = translated.GetValueOrDefault();
        return translated.HasValue;
    }
}

public interface IChatTextSearchSurface
{
    bool TryGetMatchCenter(int localIndex, Visual relativeTo, out Point center);
}

public sealed class ChatTextSearchSurfaceRegistry
{
    public event Action<ChatPresentationRow>? SurfaceChanged;

    private readonly Dictionary<ChatPresentationRow, Entry> entries = new(ReferenceEqualityComparer.Instance);
    private long _nextRegistrationId;

    public IDisposable Register(ChatPresentationRow row, IChatTextSearchSurface surface)
    {
        var id = ++_nextRegistrationId;
        entries[row] = new Entry(id, surface);
        SurfaceChanged?.Invoke(row);
        return new Registration(this, row, id);
    }

    public bool TryGet(ChatPresentationRow row, [NotNullWhen(true)] out IChatTextSearchSurface? surface)
    {
        if (entries.TryGetValue(row, out var entry))
        {
            surface = entry.Surface;
            return true;
        }

        surface = null;
        return false;
    }

    public void NotifyChanged(ChatPresentationRow? row)
    {
        if (row is not null && entries.ContainsKey(row))
        {
            SurfaceChanged?.Invoke(row);
        }
    }

    private void Unregister(ChatPresentationRow row, long id)
    {
        if (!entries.TryGetValue(row, out var entry) || entry.Id != id) return;
        entries.Remove(row);
        SurfaceChanged?.Invoke(row);
    }

    private readonly record struct Entry(long Id, IChatTextSearchSurface Surface);

    private sealed class Registration(ChatTextSearchSurfaceRegistry owner, ChatPresentationRow row, long id) : IDisposable
    {
        private ChatTextSearchSurfaceRegistry? _currentOwner = owner;

        public void Dispose()
        {
            var value = Interlocked.Exchange(ref _currentOwner, null);
            value?.Unregister(row, id);
        }
    }
}