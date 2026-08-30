using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reactive;
using System.Reactive.Subjects;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Automation;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Messages;
using Everywhere.Storage;
using Everywhere.Utilities;
using LiveMarkdown.Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ShadUI;

namespace Everywhere.Chat;

public sealed partial class ChatContextManager :
    ObservableObject,
    IChatContextManager,
    IAsyncInitializer,
    IRecipient<ChatContextMetadataChangedMessage>,
    IDisposable
{
    public ChatContext Current
    {
        get
        {
            if (_current is not null) return _current;

            CreateNew();
            return _current;
        }
    }

    public ChatContextMetadata? CurrentMetadata
    {
        get => Current.Metadata;
        set
        {
            Dispatcher.UIThread.VerifyAccess();
            if (value is null) return;

            if (value.Id == Guid.Empty)
                throw new ArgumentException("The provided chat context does not have a valid ID.", nameof(value));

            if (!_metadataMap.ContainsKey(value.Id))
                throw new ArgumentException("The provided chat context is not part of the history.", nameof(value));

            var previous = _current;
            if (previous?.Metadata.Id == value.Id) return;
            var switchVersion = Interlocked.Increment(ref _currentSwitchVersion);
            OnPropertyChanged();

            SwitchCurrentAsync(value.Id, previous, switchVersion).Detach(_logger.ToExceptionHandler());
        }
    }

    IRelayCommand IChatContextManager.UpdateRecentHistoryCommand => UpdateRecentHistoryCommand;

    /// <inheritdoc />
    public IReadOnlyBindableList<ChatContextHistory> AllHistory { get; }

    /// <inheritdoc />
    public string? HistorySearchQuery
    {
        get;
        set
        {
            if (!SetProperty(ref field, value)) return;

            var normalized = NormalizeHistorySearchQuery(value);
            if (string.Equals(normalized, _normalizedHistorySearchQuery, StringComparison.Ordinal)) return;

            _normalizedHistorySearchQuery = normalized;
            RestartHistoryMaterialization();
        }
    }

    /// <inheritdoc />
    public bool HistorySearchIncludesContent
    {
        get;
        set
        {
            if (!SetProperty(ref field, value) || _normalizedHistorySearchQuery is null) return;

            RestartHistoryMaterialization();
        }
    }

    [ObservableProperty]
    public partial bool HasMoreItems { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial int BackgroundBusyCount { get; private set; }

    [ObservableProperty]
    public partial int BackgroundNotificationCount { get; private set; }

    [field: AllowNull, MaybeNull]
    public IRelayCommand CreateNewCommand => field ??= new RelayCommand(CreateNew, () => !IsEmptyContext(_current));

    IRelayCommand<ChatContextMetadata> IChatContextManager.RemoveCommand => RemoveCommand;

    private ICollection<ChatContextMetadata> LoadedMetadata => _metadataMap.Values;

    private ChatContext? _current;
    private int _currentSwitchVersion;

    private readonly ConcurrentDictionary<Guid, ChatContextMetadata> _metadataMap = [];
    private readonly SourceList<ChatContextMetadata> _materializedHistorySource = new();
    private readonly Subject<Unit> _historyRegrouper = new();
    private readonly IDisposable _historyConnection;
    private readonly SemaphoreSlim _historyLoadGate = new(1, 1);
    private readonly Lock _historyStateLock = new();
    private readonly List<Guid> _historyScanIds = [];
    private readonly HashSet<Guid> _rawHistoryIds = [];
    private readonly HashSet<Guid> _materializedHistoryIds = [];
    private readonly HashSet<Guid> _busyContexts = [];
    private readonly HashSet<Guid> _notificationContexts = [];

    /// <summary>
    /// A buffer for chat contexts and their metadata to be saved.
    /// Sometimes only metadata needs to be saved (e.g., when only the topic is changed), in which case the context can be null.
    /// </summary>
    private readonly Dictionary<Guid, ChatContextMetadataChangedMessage> _saveBuffer = [];

    private readonly Settings _settings;
    private readonly IChatContextStorage _chatContextStorage;
    private readonly ILogger<ChatContextManager> _logger;
    private readonly DebounceExecutor<ChatContextManager, ThreadingTimerImpl> _saveDebounceExecutor;

    private CancellationTokenSource _historyGenerationCancellation = new();
    private string? _normalizedHistorySearchQuery;
    private Guid? _storageCursor;
    private int _historyScanIndex;
    private int _historyGeneration;
    private int _activeHistorySessions;
    private bool _rawHistoryExhausted;
    private bool _isDisposed;

    public ChatContextManager(
        Settings settings,
        IChatContextStorage chatContextStorage,
        ILogger<ChatContextManager> logger)
    {
        _settings = settings;
        _chatContextStorage = chatContextStorage;
        _logger = logger;

        AllHistory = _materializedHistorySource
            .Connect()
            .AutoRefresh(static metadata => metadata.DateModified)
            .GroupOn(GetHumanizedDate, _historyRegrouper)
            .Transform(static group => new ChatContextHistory(group))
            .DisposeMany()
            .Sort(SortExpressionComparer<ChatContextHistory>.Ascending(static history => history.Date))
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _historyConnection);

        _saveDebounceExecutor = new DebounceExecutor<ChatContextManager, ThreadingTimerImpl>(
            () => this,
            static that =>
            {
                List<ChatContextMetadataChangedMessage> messages;
                lock (that._saveBuffer)
                {
                    // ToList is better than ToArray (less allocation)
                    // ↑ seems to be wrong in dotnet 10. ToArray is better!
                    messages = [.. that._saveBuffer.Values];
                    that._saveBuffer.Clear();
                }
                SaveMessagesAsync(that, messages).Detach(that._logger.ToExceptionHandler());

                static async Task SaveMessagesAsync(ChatContextManager that, List<ChatContextMetadataChangedMessage> messages)
                {
                    // ReSharper disable once ForeachCanBePartlyConvertedToQueryUsingAnotherGetEnumerator
                    foreach (var message in messages)
                    {
                        if (IsEmptyContext(message.Context) || message.Metadata.IsTemporary) continue;

                        try
                        {
                            if (message.Context is not null) await that._chatContextStorage.SaveChatContextAsync(message.Context);
                            else await that._chatContextStorage.SaveChatContextMetadataAsync(message.Metadata);
                        }
                        catch (Exception ex)
                        {
                            that._logger.LogError(ex, "Failed to save chat context {ChatContextId}", message.Metadata.Id);
                        }
                    }
                }
            },
            TimeSpan.FromSeconds(0.5))
        {
            // A continuously streaming response must still be persisted periodically even
            // though every new token resets the trailing debounce interval.
            MaximumDelay = TimeSpan.FromSeconds(2)
        };

        WeakReferenceMessenger.Default.Register(this);

        Task.Run(CleanupUnusedWorkingDirectories).Detach(logger.ToExceptionHandler());
    }

    /// <summary>
    /// Handles chat context changed events.
    /// </summary>
    /// <param name="message"></param>
    public void Receive(ChatContextMetadataChangedMessage message)
    {
        switch (message.PropertyName)
        {
            case nameof(ChatContextMetadata.States):
            {
                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    if (message.Metadata.States.HasFlag(ChatContextMetadataStates.Busy)) _busyContexts.Add(message.Metadata.Id);
                    else _busyContexts.Remove(message.Metadata.Id);
                    if (message.Metadata.States.HasFlag(ChatContextMetadataStates.HasNotification)) _notificationContexts.Add(message.Metadata.Id);
                    else _notificationContexts.Remove(message.Metadata.Id);

                    var currentId = _current?.Metadata.Id;
                    BackgroundBusyCount = _busyContexts.AsValueEnumerable().Count(id => id != currentId);
                    BackgroundNotificationCount = _notificationContexts.AsValueEnumerable().Count(id => id != currentId);
                });
                break;
            }
            case nameof(ChatContextMetadata.DateModified):
            case nameof(ChatContextMetadata.Topic):
            {
                lock (_saveBuffer)
                {
                    ref var valueRef = ref CollectionsMarshal.GetValueRefOrAddDefault(_saveBuffer, message.Metadata.Id, out _);
                    if (valueRef is null) valueRef = message;
                    else
                    {
                        valueRef.Context ??= message.Context;
                        valueRef.Metadata = message.Metadata;
                    }
                }
                _saveDebounceExecutor.Trigger();

                Dispatcher.UIThread.PostOnDemand(() =>
                {
                    CreateNewCommand.NotifyCanExecuteChanged();
                    HandleHistoryMetadataChanged(
                        message.Metadata,
                        message.PropertyName == nameof(ChatContextMetadata.DateModified));
                });
                break;
            }
        }
    }

    /// <summary>
    /// Delete all directories in _runtimeConstantProvider.EnsureWritableDataFolderPath($"plugins") that named with date (yyyy-MM-dd)
    /// </summary>
    private void CleanupUnusedWorkingDirectories()
    {
        var regex = WorkingDirectoryRegex();
        var pluginsDir = RuntimeConstants.EnsureWritableDataFolderPath("plugins");
        foreach (var dir in Directory.GetDirectories(pluginsDir))
        {
            var dirName = Path.GetFileName(dir);
            if (!regex.IsMatch(dirName)) continue;

            if (!DateTime.TryParseExact(dirName, "yyyy-MM-dd", null, DateTimeStyles.None, out var dirDate))
                continue;

            // If the directory is 3 days later and is empty, delete it
            if ((DateTime.Now - dirDate).TotalDays > 3 && !Directory.EnumerateFileSystemEntries(dir).AsValueEnumerable().Any())
            {
                try
                {
                    Directory.Delete(dir); // do not use recursive delete
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete unused working directory: {Directory}", dir);
                }
            }
        }
    }

    [RelayCommand]
    private void UpdateRecentHistory() =>
        Dispatcher.UIThread.PostOnDemand(() => RestartHistoryMaterializationCore(resetRawHistory: true));

    [MemberNotNull(nameof(_current))]
    private void CreateNew()
    {
        if (IsEmptyContext(_current)) return;
        Interlocked.Increment(ref _currentSwitchVersion);

        var isCurrentTemporary = _current?.Metadata.IsTemporary is true;
        if (isCurrentTemporary)
        {
            // Remove the temporary chat context before creating a new one
            // Temporary chat contexts are not saved to storage, so no need to delete from storage.
            if (_metadataMap.Remove(_current!.Metadata.Id, out var removed))
            {
                RemoveHistoryMetadata(removed);
            }
        }

        _current = new ChatContext
        {
            Metadata =
            {
                IsTemporary = _settings.ChatWindow.TemporaryChatMode switch
                {
                    TemporaryChatMode.RememberLast => isCurrentTemporary,
                    TemporaryChatMode.Always => true,
                    _ => false
                },
            },
        };
        _metadataMap[_current.Metadata.Id] = _current.Metadata;
        // After created, the chat context is not added to the storage yet.
        // It will be added when it's property has changed.

        AddRecentHistoryMetadata(_current.Metadata);
        NotifyCurrentChanged();
    }

    private bool CanRemove => _metadataMap.Count > 1 || !IsEmptyContext(_current);

    [RelayCommand(CanExecute = nameof(CanRemove))]
    private void Remove(ChatContextMetadata metadata)
    {
        // delete in background
        Task.Run(async () =>
            {
                metadata.IsTemporaryDeleted = true;

                // If the current chat context is being removed, we need to set a new current context
                if (metadata.Id == _current?.Metadata.Id)
                {
                    await LoadRecentAsCurrentAsync().ConfigureAwait(false);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var progress = new Progress<double>();
                    var currentProgress = 0d;
                    var timer = new DispatcherTimer(
                        TimeSpan.FromSeconds(1),
                        DispatcherPriority.Normal,
                        delegate
                        {
                            currentProgress += 0.2d;
                            progress.To<IProgress<double>>().Report(currentProgress);
                        });
                    ToastManager
                        .Create(
                            new FormattedDynamicLocaleKey(
                                LocaleKey.ChatContextManager_DeletingToast_Content,
                                new DirectLocaleKey(metadata.ActualTopic ?? string.Empty)).ToString())
                        .WithProgress(progress)
                        .WithDurationSeconds(5d)
                        .WithAction(DynamicLocaleKey.Resolve(LocaleKey.Common_Undo), ButtonStyle.Ghost)
                        .OnBottomLeft()
                        .ShowInfoAsync()
                        .ContinueWith(
                            t =>
                            {
                                // This continuation runs when the toast is dismissed, either by the timer or by user action (Undo).
                                // It should be UI thread here.
                                Debug.Assert(Dispatcher.UIThread.CheckAccess());

                                timer.Stop();
                                if (t.Result != ToastResult.ActionButtonClicked)
                                {
                                    Task.Run(ExecuteDeleteAsync);
                                }
                                else
                                {
                                    metadata.IsTemporaryDeleted = false;
                                    AddRecentHistoryMetadata(metadata);
                                    RemoveCommand.NotifyCanExecuteChanged();
                                }
                            },
                            TaskContinuationOptions.ExecuteSynchronously);

                    RemoveHistoryMetadata(metadata);
                    RemoveCommand.NotifyCanExecuteChanged();
                });
            })
            .Detach(_logger.ToExceptionHandler());

        async Task ExecuteDeleteAsync()
        {
            try
            {
                metadata.States = ChatContextMetadataStates.None;
                _metadataMap.TryRemove(metadata.Id, out _);

                await _chatContextStorage.DeleteChatContextsAsync([metadata.Id]).ConfigureAwait(false);
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    RemoveHistoryMetadata(metadata);
                    RemoveCommand.NotifyCanExecuteChanged();
                });
            }
        }
    }

    /// <summary>
    /// Loads the most recently modified chat context as current.
    /// </summary>
    private async Task LoadRecentAsCurrentAsync()
    {
        var switchVersion = Interlocked.Increment(ref _currentSwitchVersion);
        ChatContext? next = null;

        // Load the most recently modified chat context that is not marked as temporary deleted
        if (LoadedMetadata
                .AsValueEnumerable()
                .Where(m => !m.IsTemporaryDeleted)
                .OrderByDescending(c => c.DateModified)
                .FirstOrDefault() is { } historyItem)
        {
            // Switch to the most recently modified chat context
            next = await LoadChatContextAsync(historyItem.Id, false).ConfigureAwait(false);
        }

        if (switchVersion != Volatile.Read(ref _currentSwitchVersion))
        {
            if (next is not null) await Dispatcher.UIThread.InvokeAsync(next.Dispose);
            return;
        }

        if (next is not null)
        {
            await Dispatcher.UIThread.InvokeAsync(() => next.Presentation.PrepareInitialWindowAsync(CancellationToken.None));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (switchVersion != Volatile.Read(ref _currentSwitchVersion))
            {
                next?.Dispose();
                return;
            }

            if (next is null)
            {
                _current = null;
                CreateNew();
            }
            else
            {
                _current = next;
                NotifyCurrentChanged();
            }

        });
    }

    public Task<ChatContext?> LoadChatContextAsync(ChatContextMetadata metadata, CancellationToken cancellationToken = default) =>
        metadata.Id == _current?.Metadata.Id ? Task.FromResult<ChatContext?>(_current) : LoadChatContextAsync(metadata.Id, false, cancellationToken);

    private async Task SwitchCurrentAsync(Guid id, ChatContext? previous, int switchVersion)
    {
        var next = await LoadChatContextAsync(id, false, CancellationToken.None).ConfigureAwait(false);
        if (switchVersion != Volatile.Read(ref _currentSwitchVersion))
        {
            if (next is not null) await Dispatcher.UIThread.InvokeAsync(next.Dispose);
            return;
        }

        if (next is not null)
        {
            // Entering the dispatcher after storage deserialization also acts as a FIFO barrier for
            // ThreadSafeObservableStringBuilder appends already posted at Normal priority. The
            // bounded tail window therefore captures committed text and version pairs before it is
            // published as Current, while older turns remain UI-unmaterialized.
            await Dispatcher.UIThread.InvokeAsync(() => next.Presentation.PrepareInitialWindowAsync(CancellationToken.None));
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (switchVersion != Volatile.Read(ref _currentSwitchVersion))
            {
                next?.Dispose();
                return;
            }

            if (next is null)
            {
                CreateNew();
            }
            else
            {
                _current = next;
                NotifyCurrentChanged();
            }


            // Removing the previous context in the same collection-change pass can make Avalonia
            // observe an intermediate index map. Keep the established background-priority cleanup.
            Dispatcher.UIThread.Post(
                () =>
                {
                    CreateNewCommand.NotifyCanExecuteChanged();

                    if (previous is not null &&
                        (IsEmptyContext(previous) || previous.Metadata.IsTemporary) &&
                        _metadataMap.Remove(previous.Metadata.Id, out _))
                    {
                        RemoveHistoryMetadata(previous.Metadata);
                    }

                    RemoveCommand.NotifyCanExecuteChanged();

                    var currentId = _current?.Metadata.Id;
                    BackgroundBusyCount = _busyContexts.AsValueEnumerable().Count(contextId => contextId != currentId);
                    BackgroundNotificationCount = _notificationContexts.AsValueEnumerable().Count(contextId => contextId != currentId);
                },
                DispatcherPriority.Background);
        });
    }

    private async Task<ChatContext?> LoadChatContextAsync(Guid id, bool deleteIfFailed, CancellationToken cancellationToken = default)
    {
        try
        {
            var chatContext = await _chatContextStorage.GetChatContextAsync(id, cancellationToken).ConfigureAwait(false);
            if (!IsEmptyContext(chatContext)) return chatContext;

            // If the loaded chat context is empty, it means it's corrupted or failed to load. We should delete it from storage and remove it from history.
            await _chatContextStorage.DeleteChatContextsAsync([id], cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            ex = HandledSystemException.Handle(ex);
            _logger.LogError(ex, "Failed to load chat context {ChatContextId}", id);

            await Dispatcher.UIThread.InvokeOnDemandAsync(() =>
            {
                ToastManager
                    .Error(
                        LocaleResolver.Common_Error,
                        new FormattedDynamicLocaleKey(
                            LocaleKey.ChatContextManager_LoadChatContextFailedToast_Content,
                            ex.GetFriendlyMessage()));
            });

            if (deleteIfFailed)
            {
                await _chatContextStorage.DeleteChatContextsAsync([id], cancellationToken).ConfigureAwait(false);
            }

            return null;
        }
    }

    /// <summary>
    /// Notifies that the current chat context has changed.
    /// </summary>
    private void NotifyCurrentChanged()
    {
        Dispatcher.UIThread.VerifyAccess();
        OnPropertyChanged(nameof(Current));
        OnPropertyChanged(nameof(CurrentMetadata));
        RemoveCommand.NotifyCanExecuteChanged();
        CreateNewCommand.NotifyCanExecuteChanged();
    }

    /// <inheritdoc />
    public IIncrementalLoadSession BeginLoadSession()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        Interlocked.Increment(ref _activeHistorySessions);
        Dispatcher.UIThread.PostOnDemand(UpdateHistoryBusyState);
        return new IncrementalLoadSession(this);
    }

    private async ValueTask<IncrementalLoadResult> LoadMoreHistoryAsync(int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        await _historyLoadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int generation;
                string? query;
                bool searchIncludesContent;
                CancellationToken generationToken;
                lock (_historyStateLock)
                {
                    generation = _historyGeneration;
                    query = _normalizedHistorySearchQuery;
                    searchIncludesContent = HistorySearchIncludesContent;
                    generationToken = _historyGenerationCancellation.Token;
                }

                try
                {
                    return await LoadMoreHistoryCoreAsync(
                        count,
                        generation,
                        query,
                        searchIncludesContent,
                        generationToken,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (generationToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // Query changes are internal retargeting, not cancellation of the caller's
                    // viewport-fill operation. Retry the same requested page against the latest
                    // generation so the behavior remains independent of search state.
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to incrementally load chat context history");
            ToastManager.Error(LocaleResolver.Common_Error, ex.GetFriendlyMessage());

            return new IncrementalLoadResult(0, HasMoreItems);
        }
        finally
        {
            _historyLoadGate.Release();
        }
    }

    private async Task<IncrementalLoadResult> LoadMoreHistoryCoreAsync(
        int count,
        int generation,
        string? query,
        bool searchIncludesContent,
        CancellationToken generationToken,
        CancellationToken callerToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(generationToken, callerToken);
        var cancellationToken = linkedCancellation.Token;
        var matches = new List<ChatContextMetadata>(count);
        var pattern = query is null ? null : new TextSearchPattern(query);

        while (matches.Count < count)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Guid? candidateId = null;
            var mustFetch = false;
            lock (_historyStateLock)
            {
                ThrowIfHistoryGenerationChanged(generation, cancellationToken);
                if (_historyScanIndex < _historyScanIds.Count)
                {
                    candidateId = _historyScanIds[_historyScanIndex++];
                }
                else if (!_rawHistoryExhausted)
                {
                    mustFetch = true;
                }
            }

            if (mustFetch)
            {
                await FetchRawHistoryPageAsync(Math.Max(count - matches.Count, 20), generation, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (candidateId is not { } id) break;
            if (!_metadataMap.TryGetValue(id, out var metadata) || metadata.IsTemporaryDeleted) continue;
            if (await MatchesHistorySearchAsync(metadata, pattern, searchIncludesContent, cancellationToken).ConfigureAwait(false))
            {
                matches.Add(metadata);
            }
        }

        bool hasMoreItems;
        lock (_historyStateLock)
        {
            ThrowIfHistoryGenerationChanged(generation, cancellationToken);
            hasMoreItems = _historyScanIndex < _historyScanIds.Count || !_rawHistoryExhausted;
        }

        return await Dispatcher.UIThread.InvokeAsync(
            () =>
            {
                lock (_historyStateLock)
                {
                    ThrowIfHistoryGenerationChanged(generation, cancellationToken);

                    matches.RemoveAll(metadata =>
                        metadata.IsTemporaryDeleted ||
                        !_metadataMap.ContainsKey(metadata.Id) ||
                        !_materializedHistoryIds.Add(metadata.Id));
                    if (matches.Count > 0)
                    {
                        _materializedHistorySource.AddRange(matches);
                        RemoveCommand.NotifyCanExecuteChanged();
                    }

                    HasMoreItems = hasMoreItems;
                    return new IncrementalLoadResult(matches.Count, hasMoreItems);
                }
            },
            DispatcherPriority.Normal,
            cancellationToken);
    }

    private async Task FetchRawHistoryPageAsync(
        int count,
        int generation,
        CancellationToken cancellationToken)
    {
        Guid? cursor;
        lock (_historyStateLock)
        {
            cursor = _storageCursor;
        }

        var fetched = new List<ChatContextMetadata>(count);
        await foreach (var metadata in _chatContextStorage.QueryChatContextsAsync(
                           count,
                           ChatContextOrderBy.UpdatedAt,
                           true,
                           cursor,
                           cancellationToken).ConfigureAwait(false))
        {
            fetched.Add(metadata);
        }

        lock (_historyStateLock)
        {
            ThrowIfHistoryGenerationChanged(generation, cancellationToken);
            foreach (var metadata in fetched)
            {
                var canonical = _metadataMap.AddOrUpdate(
                    metadata.Id,
                    metadata,
                    (_, existing) =>
                    {
                        metadata.IsTemporaryDeleted = existing.IsTemporaryDeleted;
                        return existing;
                    });

                _storageCursor = metadata.Id;
                if (_rawHistoryIds.Add(canonical.Id) && !canonical.IsTemporaryDeleted)
                {
                    _historyScanIds.Add(canonical.Id);
                }
            }

            if (fetched.Count < count)
            {
                _rawHistoryExhausted = true;
            }
        }
    }

    private async ValueTask<bool> MatchesHistorySearchAsync(
        ChatContextMetadata metadata,
        TextSearchPattern? pattern,
        bool includesContent,
        CancellationToken cancellationToken)
    {
        if (pattern is null) return true;
        if (metadata.ActualTopic?.Contains(pattern.Query, StringComparison.OrdinalIgnoreCase) is true) return true;
        if (!includesContent) return false;

        try
        {
            var context = metadata.Id == _current?.Metadata.Id ?
                _current :
                await _chatContextStorage.GetChatContextAsync(metadata.Id, cancellationToken).ConfigureAwait(false);
            return context is not null && ChatTextSearcher.Contains(
                context,
                pattern,
                ChatTextSearcher.SharedMarkdownTextProjector,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Filtering is read-only and best-effort. One damaged history entry must not abort the
            // ordered scan or reuse the interactive load path that can delete data and show a toast.
            _logger.LogWarning(ex, "Failed to inspect chat context {ChatContextId} while filtering history", metadata.Id);
            return false;
        }
    }

    private void RestartHistoryMaterialization() =>
        Dispatcher.UIThread.PostOnDemand(() => RestartHistoryMaterializationCore());

    private void RestartHistoryMaterializationCore(bool resetRawHistory = false)
    {
        Dispatcher.UIThread.VerifyAccess();

        CancellationTokenSource previousCancellation;
        bool hasMoreItems;
        lock (_historyStateLock)
        {
            _historyGeneration++;
            previousCancellation = _historyGenerationCancellation;
            _historyGenerationCancellation = new CancellationTokenSource();

            if (resetRawHistory)
            {
                _metadataMap.Clear();
                _rawHistoryIds.Clear();
                if (_current is not null)
                {
                    _metadataMap[_current.Metadata.Id] = _current.Metadata;
                    _rawHistoryIds.Add(_current.Metadata.Id);
                }

                _storageCursor = null;
                _rawHistoryExhausted = false;
            }

            _historyScanIds.Clear();
            _historyScanIds.AddRange(
                _metadataMap.Values
                    .Where(static metadata => !metadata.IsTemporaryDeleted)
                    .OrderByDescending(static metadata => metadata.DateModified)
                    .ThenByDescending(static metadata => metadata.Id)
                    .Select(static metadata => metadata.Id));
            _historyScanIndex = 0;
            _materializedHistoryIds.Clear();
            hasMoreItems = _historyScanIds.Count > 0 || !_rawHistoryExhausted;
        }

        previousCancellation.Cancel();
        previousCancellation.Dispose();
        _materializedHistorySource.Clear();
        HasMoreItems = hasMoreItems;

        // A reset must wake the behavior even when the boolean value stayed true. The ScrollViewer
        // also returns to the top because the materialized result was cleared.
        OnPropertyChanged(nameof(HasMoreItems));
    }

    private void AddRecentHistoryMetadata(ChatContextMetadata metadata) =>
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            lock (_historyStateLock)
            {
                _rawHistoryIds.Add(metadata.Id);
            }

            if (_normalizedHistorySearchQuery is not null)
            {
                RestartHistoryMaterializationCore();
                return;
            }

            lock (_historyStateLock)
            {
                if (metadata.IsTemporaryDeleted || !_materializedHistoryIds.Add(metadata.Id)) return;
                _materializedHistorySource.Add(metadata);
            }
        });

    private void RemoveHistoryMetadata(ChatContextMetadata metadata) =>
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            lock (_historyStateLock)
            {
                if (!_materializedHistoryIds.Remove(metadata.Id)) return;
                _materializedHistorySource.Remove(metadata);
            }
        });

    private void HandleHistoryMetadataChanged(ChatContextMetadata metadata, bool invalidatesStorageCursor)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (invalidatesStorageCursor)
        {
            lock (_historyStateLock)
            {
                // A modified cursor is no longer a valid boundary in UpdatedAt order. Restarting the
                // raw cursor is cheap because already-known IDs are deduplicated before entering the
                // scan, and it prevents a selected old conversation from making intermediate rows skip.
                _storageCursor = null;
                _rawHistoryExhausted = false;
            }
        }

        if (_normalizedHistorySearchQuery is not null)
        {
            RestartHistoryMaterializationCore();
            return;
        }

        lock (_historyStateLock)
        {
            if (!_materializedHistoryIds.Contains(metadata.Id)) return;
        }

        _historyRegrouper.OnNext(Unit.Default);
    }

    private void EndLoadSession()
    {
        Interlocked.Decrement(ref _activeHistorySessions);
        Dispatcher.UIThread.PostOnDemand(UpdateHistoryBusyState);
    }

    private void UpdateHistoryBusyState() => IsBusy = Volatile.Read(ref _activeHistorySessions) > 0;

    private void ThrowIfHistoryGenerationChanged(int generation, CancellationToken cancellationToken)
    {
        if (generation != Volatile.Read(ref _historyGeneration))
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static HumanizedDate GetHumanizedDate(ChatContextMetadata metadata) =>
        (DateTimeOffset.UtcNow - metadata.DateModified).TotalDays switch
        {
            < 1 => HumanizedDate.Today,
            < 2 => HumanizedDate.Yesterday,
            < 7 => HumanizedDate.LastWeek,
            < 30 => HumanizedDate.LastMonth,
            < 365 => HumanizedDate.LastYear,
            _ => HumanizedDate.Earlier
        };

    private static string? NormalizeHistorySearchQuery(string? query) =>
        string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    public AsyncInitializerIndex Index => AsyncInitializerIndex.Startup;

    /// <summary>
    /// Defers history I/O until the history viewport requests its first page.
    /// </summary>
    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// Releases the manager-owned DynamicData pipeline and cancels pending history searches.
    /// </summary>
    public void Dispose()
    {
        if (_isDisposed) return;

        _isDisposed = true;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _historyGenerationCancellation.Cancel();
        _historyGenerationCancellation.Dispose();
        _historyConnection.Dispose();
        _materializedHistorySource.Dispose();
        _historyRegrouper.Dispose();
        _historyLoadGate.Dispose();
        _saveDebounceExecutor.Dispose();
    }

    private sealed class IncrementalLoadSession(ChatContextManager owner) : IIncrementalLoadSession
    {
        private int _isDisposed;

        public ValueTask<IncrementalLoadResult> LoadMoreAsync(
            int count,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDisposed) != 0, this);
            return owner.LoadMoreHistoryAsync(count, cancellationToken);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _isDisposed, 1) != 0) return;
            owner.EndLoadSession();
        }
    }

    private static bool IsEmptyContext([NotNullWhen(true)] ChatContext? chatContext) => chatContext is { Count: 1 };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex WorkingDirectoryRegex();
}

public static class ChatContextManagerExtensions
{
    public static IServiceCollection AddChatContextManager(this IServiceCollection services)
    {
        services.AddSingleton<ChatContextManager>();
        services.AddSingleton<IChatContextManager>(x => x.GetRequiredService<ChatContextManager>());
        services.AddTransient<IAsyncInitializer>(x => x.GetRequiredService<ChatContextManager>());
        return services;
    }
}
