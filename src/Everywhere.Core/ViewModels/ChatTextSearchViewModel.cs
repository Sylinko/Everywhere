using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Everywhere.Chat;
using Everywhere.Views;
using LiveMarkdown.Avalonia;
using Lucide.Avalonia;
using Serilog;

namespace Everywhere.ViewModels;

/// <summary>
/// Defines the scope of a chat text search operation, allowing filtering of search results based on the origin of the messages.
/// </summary>
public enum ChatTextSearchScope
{
    All,
    User,
    Assistant,
}

/// <summary>
/// Searches the complete current chat model while resolving visual rows only when navigation needs
/// them. Markdown projections are cached by span identity and committed source version, so query
/// changes do not reparse unchanged messages and UI windowing does not change global match counts.
/// </summary>
public sealed partial class ChatTextSearchViewModel : ObservableObject, IDisposable
{
    public TextSearchPattern? ActivePattern { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchScopeIcon))]
    [NotifyPropertyChangedFor(nameof(SearchScopeToolTipKey))]
    public partial ChatTextSearchScope SearchScope { get; private set; }

    public LucideIconKind SearchScopeIcon => SearchScope switch
    {
        ChatTextSearchScope.User => LucideIconKind.User,
        ChatTextSearchScope.Assistant => LucideIconKind.Bot,
        _ => LucideIconKind.MessagesSquare,
    };

    public IDynamicLocaleKey SearchScopeToolTipKey => SearchScope switch
    {
        ChatTextSearchScope.User => UserScopeToolTipKey,
        ChatTextSearchScope.Assistant => AssistantScopeToolTipKey,
        _ => AllScopeToolTipKey,
    };

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    public partial string? Query { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatches))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchResultCountText))]
    public partial int CurrentIndex { get; private set; } = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SearchResultCountText))]
    [NotifyPropertyChangedFor(nameof(HasMatches))]
    public partial int MatchCount { get; private set; }

    public string SearchResultCountText => MatchCount == 0 ? "0/0" : $"{CurrentIndex + 1}/{MatchCount}";

    public bool HasMatches => !IsBusy && MatchCount > 0;

    /// <summary>
    /// Raised when realized surfaces must apply or clear the active search pattern.
    /// </summary>
    public event EventHandler? VisualStateChanged;

    /// <summary>
    /// Raised when realized surfaces only need to move the current-match highlight.
    /// </summary>
    internal event EventHandler? CurrentMatchChanged;

    /// <summary>
    /// Raised when the selected logical match should be revealed and brought into the viewport.
    /// </summary>
    public event EventHandler? NavigationRequested;

    /// <summary>
    /// Raised when the search input should receive keyboard focus.
    /// </summary>
    public event EventHandler? FocusRequested;

    private static readonly IDynamicLocaleKey AllScopeToolTipKey = new DynamicLocaleKey(LocaleKey.ChatWindow_TextSearchScope_All_ToolTip);
    private static readonly IDynamicLocaleKey UserScopeToolTipKey = new DynamicLocaleKey(LocaleKey.ChatWindow_TextSearchScope_User_ToolTip);
    private static readonly IDynamicLocaleKey AssistantScopeToolTipKey = new DynamicLocaleKey(LocaleKey.ChatWindow_TextSearchScope_Assistant_ToolTip);

    private readonly IChatContextManager _chatContextManager;
    private readonly List<SearchSourceState> _sourceStates = [];
    private readonly Dictionary<object, SearchSourceState> _statesBySource = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<AssistantChatMessage> _subscribedAssistants = new(ReferenceEqualityComparer.Instance);
    private readonly List<ChatTextSearchMatch> _matches = [];

    private ChatContext? _context;
    private IDisposable? _contextSubscription;
    private CancellationTokenSource? _projectionCancellation;
    private CancellationTokenSource? _matchCancellation;
    private long _searchGeneration;
    private long _projectionOperation;
    private long _matchingGeneration = -1;
    private long _publishedGeneration = -1;
    private bool _projectionRefreshRunning;
    private bool _sourceReconciliationQueued;
    private bool _isDisposed;

    public ChatTextSearchViewModel(IChatContextManager chatContextManager)
    {
        _chatContextManager = chatContextManager;
        chatContextManager.PropertyChanged += HandleChatContextManagerPropertyChanged;
        AttachContext(chatContextManager.Current);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _chatContextManager.PropertyChanged -= HandleChatContextManagerPropertyChanged;
        DetachContext();
        CancelProjectionRefresh();
        CancelMatch();
    }

    /// <summary>
    /// Accepts the exact projection produced by a realized renderer when it represents the current
    /// source object and committed version for the logical span.
    /// </summary>
    internal void AcceptRenderedProjection(ChatPresentationRow row, ObservableStringBuilder source, MarkdownTextProjection projection)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (row is not AssistantTextOutputPresentationRow textRow ||
            !_statesBySource.TryGetValue(textRow.TextSpan, out var state) ||
            !state.AcceptRenderedProjection(source, projection))
        {
            return;
        }

        if (IsIncluded(state)) RestartMatchingForProjectionChange();
    }

    internal bool IsRowIncluded(ChatPresentationRow row) => SearchScope switch
    {
        ChatTextSearchScope.User => row is ChatMessagePresentationRow { Node.Message: UserChatMessage },
        ChatTextSearchScope.Assistant => row is AssistantTextOutputPresentationRow,
        _ => row is ChatMessagePresentationRow { Node.Message: UserChatMessage } or AssistantTextOutputPresentationRow,
    };

    internal int GetCurrentLocalIndex(ChatPresentationRow row)
    {
        var current = GetCurrentMatch();
        return current is { } match && MatchBelongsToRow(match, row) ? match.LocalIndex : -1;
    }

    internal ChatTextSearchMatch? GetCurrentMatch() =>
        !IsBusy && CurrentIndex >= 0 && CurrentIndex < _matches.Count ? _matches[CurrentIndex] : null;

    [RelayCommand]
    private void OpenSearch()
    {
        IsOpen = true;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void CloseSearch() => IsOpen = false;

    [RelayCommand]
    private void PreviousResult() => MoveCurrent(-1);

    [RelayCommand]
    private void NextResult() => MoveCurrent(1);

    [RelayCommand]
    private void CycleSearchScope() => SearchScope = SearchScope switch
    {
        ChatTextSearchScope.All => ChatTextSearchScope.User,
        ChatTextSearchScope.User => ChatTextSearchScope.Assistant,
        _ => ChatTextSearchScope.All,
    };

    partial void OnIsOpenChanged(bool value)
    {
        if (value)
        {
            StartRefresh(clearMatches: true);
            return;
        }

        CancelProjectionRefresh();
        CancelMatch();
        _searchGeneration++;
        ActivePattern = null;
        IsBusy = false;
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
        ReplaceMatches([]);
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnQueryChanged(string? value)
    {
        if (IsOpen) StartRefresh(clearMatches: true);
    }

    // ReSharper disable once UnusedParameterInPartialMethod
    partial void OnSearchScopeChanged(ChatTextSearchScope value)
    {
        if (IsOpen) StartRefresh(clearMatches: true);
    }

    private void HandleChatContextManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IChatContextManager.Current)) return;

        var context = _chatContextManager.Current;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (ReferenceEquals(context, _chatContextManager.Current)) AttachContext(context);
        });
    }

    private void AttachContext(ChatContext? context)
    {
        Dispatcher.UIThread.VerifyAccess();
        CancelProjectionRefresh();
        CancelMatch();
        DetachContext();

        _context = context;
        if (context is not null)
        {
            _contextSubscription = context.ConnectDisplayItems().Subscribe(_ => RequestSourceReconciliation());
            ReconcileSources();
        }

        StartRefresh(clearMatches: true);
    }

    private void DetachContext()
    {
        _contextSubscription?.Dispose();
        _contextSubscription = null;
        _context = null;
        _sourceReconciliationQueued = false;

        foreach (var assistant in _subscribedAssistants) assistant.Spans.CollectionChanged -= HandleAssistantSpansChanged;
        _subscribedAssistants.Clear();

        foreach (var state in _statesBySource.Values) state.Dispose();
        _statesBySource.Clear();
        _sourceStates.Clear();
    }

    private void RequestSourceReconciliation()
    {
        if (_sourceReconciliationQueued) return;
        _sourceReconciliationQueued = true;
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            _sourceReconciliationQueued = false;
            ReconcileSources();
            if (IsOpen) StartRefresh(clearMatches: false);
        });
    }

    private void ReconcileSources()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_context is not { } context) return;

        var retained = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var retainedAssistants = new HashSet<AssistantChatMessage>(ReferenceEqualityComparer.Instance);
        var nextStates = new List<SearchSourceState>();
        foreach (var node in context.Items)
        {
            if (node.Message.IsHidden) continue;

            switch (node.Message)
            {
                case UserChatMessage userMessage:
                    retained.Add(node);
                    if (!_statesBySource.TryGetValue(node, out var userState))
                    {
                        userState = SearchSourceState.CreateUser(node, userMessage, HandleSourceContentChanged);
                        _statesBySource.Add(node, userState);
                    }

                    nextStates.Add(userState);
                    break;
                case AssistantChatMessage assistant:
                    retainedAssistants.Add(assistant);
                    if (_subscribedAssistants.Add(assistant)) assistant.Spans.CollectionChanged += HandleAssistantSpansChanged;

                    foreach (var span in assistant.Spans.AsValueEnumerable().OfType<AssistantChatMessageTextSpan>())
                    {
                        retained.Add(span);
                        if (!_statesBySource.TryGetValue(span, out var markdownState))
                        {
                            markdownState = SearchSourceState.CreateMarkdown(node, span, HandleSourceContentChanged);
                            _statesBySource.Add(span, markdownState);
                        }

                        nextStates.Add(markdownState);
                    }

                    break;
            }
        }

        foreach (var pair in _statesBySource.ToArray())
        {
            if (retained.Contains(pair.Key)) continue;
            _statesBySource.Remove(pair.Key);
            pair.Value.Dispose();
        }

        foreach (var assistant in _subscribedAssistants.ToArray())
        {
            if (retainedAssistants.Contains(assistant)) continue;
            assistant.Spans.CollectionChanged -= HandleAssistantSpansChanged;
            _subscribedAssistants.Remove(assistant);
        }

        _sourceStates.Clear();
        _sourceStates.AddRange(nextStates);
    }

    private void HandleAssistantSpansChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        RequestSourceReconciliation();

    private void HandleSourceContentChanged(SearchSourceState state)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (!_statesBySource.TryGetValue(state.Key, out var current) || !ReferenceEquals(current, state)) return;
            if (IsOpen && IsIncluded(state)) StartRefresh(clearMatches: false);
        });
    }

    private void StartRefresh(bool clearMatches)
    {
        CancelMatch();
        _searchGeneration++;

        if (!IsOpen || string.IsNullOrEmpty(Query))
        {
            CancelProjectionRefresh();
            ActivePattern = null;
            IsBusy = false;
            VisualStateChanged?.Invoke(this, EventArgs.Empty);
            ReplaceMatches([]);
            return;
        }

        ActivePattern = new TextSearchPattern(Query);
        IsBusy = true;
        VisualStateChanged?.Invoke(this, EventArgs.Empty);
        if (clearMatches) ReplaceMatches([]);
        StartMatchingCurrentSearch();
    }

    private void EnsureProjections()
    {
        if (_projectionRefreshRunning || !IsOpen || ActivePattern is null) return;

        var work = new List<ProjectionWork>();
        foreach (var state in _sourceStates)
        {
            if (!IsIncluded(state)) continue;
            if (state.TryCreateProjectionWork() is { } item) work.Add(item);
        }

        if (work.Count == 0)
        {
            StartMatchingCurrentSearch();
            return;
        }

        _projectionRefreshRunning = true;
        var operation = ++_projectionOperation;
        _projectionCancellation = new CancellationTokenSource();
        RefreshProjectionsAsync(work, operation, _projectionCancellation.Token).Detach();
    }

    private async Task RefreshProjectionsAsync(
        IReadOnlyList<ProjectionWork> work,
        long operation,
        CancellationToken cancellationToken)
    {
        try
        {
            var projections = await Task.Run(
                () =>
                {
                    var results = new MarkdownTextProjection[work.Count];
                    for (var i = 0; i < work.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        results[i] = ChatTextSearcher.SharedMarkdownTextProjector.Project(work[i].Snapshot, cancellationToken);
                    }

                    return results;
                },
                cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (operation != _projectionOperation || cancellationToken.IsCancellationRequested) return;

                for (var i = 0; i < work.Count; i++)
                    work[i].State.AcceptOffscreenProjection(work[i].Source, projections[i]);

                CompleteProjectionRefresh(operation);
                EnsureProjections();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to build Markdown text projections for chat search.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (operation != _projectionOperation) return;
                CompleteProjectionRefresh(operation);
                IsBusy = false;
                ReplaceMatches([]);
            });
        }
    }

    private void StartMatchingCurrentSearch()
    {
        if (!IsOpen || ActivePattern is not { } pattern) return;

        if (_sourceStates.Any(state => IsIncluded(state) && !state.IsProjectionCurrent))
        {
            EnsureProjections();
            return;
        }

        var generation = _searchGeneration;
        if (_matchingGeneration == generation || _publishedGeneration == generation) return;

        CancelMatch();
        _matchingGeneration = generation;

        var snapshots = new List<SearchSnapshot>(_sourceStates.Count);
        foreach (var state in _sourceStates)
        {
            if (!IsIncluded(state)) continue;
            if (state.CreateSearchSnapshot() is { } snapshot) snapshots.Add(snapshot);
        }

        if (snapshots.Count == 0)
        {
            _matchingGeneration = -1;
            _publishedGeneration = generation;
            IsBusy = false;
            ReplaceMatches([]);
            return;
        }

        _matchCancellation = new CancellationTokenSource();
        MatchAsync(pattern, snapshots, generation, _matchCancellation.Token).Detach();
    }

    private async Task MatchAsync(
        TextSearchPattern pattern,
        IReadOnlyList<SearchSnapshot> snapshots,
        long generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var nextMatches = await Task.Run(
                () => BuildMatches(pattern, snapshots, cancellationToken),
                cancellationToken);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _searchGeneration || cancellationToken.IsCancellationRequested) return;

                CompleteMatch(generation);
                _publishedGeneration = generation;
                IsBusy = false;
                ReplaceMatches(nextMatches);
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            Log.Error(exception, "Failed to match projected chat text.");
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _searchGeneration) return;
                CompleteMatch(generation);
                _publishedGeneration = generation;
                IsBusy = false;
                ReplaceMatches([]);
            });
        }
    }

    private static List<ChatTextSearchMatch> BuildMatches(
        TextSearchPattern pattern,
        IReadOnlyList<SearchSnapshot> snapshots,
        CancellationToken cancellationToken)
    {
        var nextMatches = new List<ChatTextSearchMatch>();
        foreach (var snapshot in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var localIndex = 0;
            if (snapshot.PlainText is { } text)
            {
                foreach (var range in pattern.FindRanges(text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nextMatches.Add(
                        new ChatTextSearchMatch(
                            snapshot.Node,
                            null,
                            0,
                            localIndex++,
                            new TextHighlightRange(range.Start + snapshot.PlainTextOffset, range.Length)));
                }

                continue;
            }

            if (snapshot.Projection is not { } projection || snapshot.Span is not { } span) continue;
            for (var bufferIndex = 0; bufferIndex < projection.Buffers.Count; bufferIndex++)
            {
                foreach (var range in pattern.FindRanges(projection.Buffers[bufferIndex].Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    nextMatches.Add(
                        new ChatTextSearchMatch(
                            snapshot.Node,
                            span,
                            bufferIndex,
                            localIndex++,
                            range));
                }
            }
        }

        return nextMatches;
    }

    private static bool MatchBelongsToRow(ChatTextSearchMatch match, ChatPresentationRow row) => row switch
    {
        ChatMessagePresentationRow messageRow => match.Span is null && ReferenceEquals(match.Node, messageRow.Node),
        AssistantTextOutputPresentationRow outputRow => match.Span is not null && ReferenceEquals(match.Span, outputRow.TextSpan),
        _ => false,
    };

    private bool IsIncluded(SearchSourceState state) => SearchScope switch
    {
        ChatTextSearchScope.User => state.Scope == ChatTextSearchScope.User,
        ChatTextSearchScope.Assistant => state.Scope == ChatTextSearchScope.Assistant,
        _ => true,
    };

    private void RestartMatchingForProjectionChange()
    {
        if (!IsOpen || ActivePattern is null) return;

        CancelMatch();
        _searchGeneration++;
        IsBusy = true;
        StartMatchingCurrentSearch();
    }

    private void ReplaceMatches(IReadOnlyList<ChatTextSearchMatch> nextMatches)
    {
        var previous = GetCurrentMatch();
        _matches.Clear();
        _matches.AddRange(nextMatches);
        MatchCount = _matches.Count;

        var nextIndex = -1;
        if (_matches.Count > 0)
        {
            nextIndex = previous is { } previousMatch ? _matches.IndexOf(previousMatch) : 0;
            if (nextIndex < 0) nextIndex = Math.Min(CurrentIndex, _matches.Count - 1);
            if (nextIndex < 0) nextIndex = 0;
        }

        CurrentIndex = nextIndex;
        CurrentMatchChanged?.Invoke(this, EventArgs.Empty);
        if (nextIndex >= 0 && (previous is null || !_matches[nextIndex].Equals(previous.Value)))
            NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void MoveCurrent(int delta)
    {
        if (IsBusy || _matches.Count == 0) return;

        var nextIndex = (CurrentIndex + delta + _matches.Count) % _matches.Count;
        if (nextIndex != CurrentIndex)
        {
            CurrentIndex = nextIndex;
            CurrentMatchChanged?.Invoke(this, EventArgs.Empty);
        }

        NavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    private void CompleteProjectionRefresh(long operation)
    {
        if (operation != _projectionOperation) return;
        _projectionRefreshRunning = false;
        _projectionCancellation?.Dispose();
        _projectionCancellation = null;
    }

    private void CancelProjectionRefresh()
    {
        _projectionOperation++;
        _projectionRefreshRunning = false;
        _projectionCancellation?.Cancel();
        _projectionCancellation?.Dispose();
        _projectionCancellation = null;
    }

    private void CompleteMatch(long generation)
    {
        if (_matchingGeneration != generation) return;
        _matchingGeneration = -1;
        _matchCancellation?.Dispose();
        _matchCancellation = null;
    }

    private void CancelMatch()
    {
        _matchingGeneration = -1;
        _matchCancellation?.Cancel();
        _matchCancellation?.Dispose();
        _matchCancellation = null;
    }

    internal readonly record struct ChatTextSearchMatch(
        ChatMessageNode Node,
        AssistantChatMessageSpan? Span,
        int BufferIndex,
        int LocalIndex,
        TextHighlightRange Range
    );

    private readonly record struct ProjectionWork(
        SearchSourceState State,
        ObservableStringBuilder Source,
        ObservableStringBuilderSnapshot Snapshot
    );

    private readonly record struct SearchSnapshot(
        ChatMessageNode Node,
        AssistantChatMessageTextSpan? Span,
        string? PlainText,
        int PlainTextOffset,
        MarkdownTextProjection? Projection
    );

    private sealed class SearchSourceState : IDisposable
    {
        public object Key { get; }
        public ChatTextSearchScope Scope => _userMessage is null ? ChatTextSearchScope.Assistant : ChatTextSearchScope.User;
        public bool IsProjectionCurrent => _markdownSource is null || _projection?.SourceVersion == _markdownSource.Version;

        private readonly ChatMessageNode _node;
        private readonly AssistantChatMessageTextSpan? _span;
        private readonly Action<SearchSourceState> _contentChanged;
        private readonly UserChatMessage? _userMessage;
        private readonly ObservableStringBuilder? _markdownSource;
        private MarkdownTextProjection? _projection;

        private SearchSourceState(
            object key,
            ChatMessageNode node,
            Action<SearchSourceState> contentChanged,
            UserChatMessage? userMessage,
            AssistantChatMessageTextSpan? span)
        {
            Key = key;
            _node = node;
            _span = span;
            _contentChanged = contentChanged;
            _userMessage = userMessage;
            _markdownSource = span?.ContentMarkdownBuilder;

            if (userMessage is not null) userMessage.PropertyChanged += HandleUserMessagePropertyChanged;
            if (_markdownSource is not null) _markdownSource.Changed += HandleMarkdownSourceChanged;
        }

        public static SearchSourceState CreateUser(
            ChatMessageNode node,
            UserChatMessage message,
            Action<SearchSourceState> contentChanged) =>
            new(node, node, contentChanged, message, null);

        public static SearchSourceState CreateMarkdown(
            ChatMessageNode node,
            AssistantChatMessageTextSpan span,
            Action<SearchSourceState> contentChanged) =>
            new(span, node, contentChanged, null, span);

        public ProjectionWork? TryCreateProjectionWork()
        {
            if (_markdownSource is null || IsProjectionCurrent) return null;
            return new ProjectionWork(this, _markdownSource, _markdownSource.CaptureSnapshot());
        }

        public bool AcceptRenderedProjection(ObservableStringBuilder source, MarkdownTextProjection value)
        {
            if (!ReferenceEquals(_markdownSource, source) || source.Version != value.SourceVersion) return false;
            if (ReferenceEquals(_projection, value)) return false;
            _projection = value;
            return true;
        }

        public void AcceptOffscreenProjection(ObservableStringBuilder source, MarkdownTextProjection value)
        {
            if (!ReferenceEquals(_markdownSource, source) || source.Version != value.SourceVersion || IsProjectionCurrent) return;
            _projection = value;
        }

        public SearchSnapshot? CreateSearchSnapshot()
        {
            if (_userMessage is not null)
            {
                return new SearchSnapshot(
                    _node,
                    null,
                    _userMessage.Content,
                    _userMessage is UserStrategyChatMessage ? 1 : 0,
                    null);
            }

            return IsProjectionCurrent && _projection is not null ? new SearchSnapshot(_node, _span, null, 0, _projection) : null;
        }

        public void Dispose()
        {
            if (_userMessage is not null) _userMessage.PropertyChanged -= HandleUserMessagePropertyChanged;
            if (_markdownSource is not null) _markdownSource.Changed -= HandleMarkdownSourceChanged;
        }

        private void HandleUserMessagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UserChatMessage.Content)) _contentChanged(this);
        }

        private void HandleMarkdownSourceChanged(in ObservableStringBuilderChangedEventArgs e) => _contentChanged(this);
    }
}