using System.Diagnostics.CodeAnalysis;
using System.Reactive.Disposables.Fluent;
using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Common;

namespace Everywhere.Chat;

/// <summary>
/// Represents one user-submitted message waiting to be steered into an active turn or drained as a follow-up turn.
/// </summary>
public sealed partial class ChatPendingInputItem : ObservableObject
{
    /// <summary>
    /// Gets the stable identifier of this queued item.
    /// </summary>
    public Guid Id { get; }

    /// <summary>
    /// Gets the queued user message payload.
    /// </summary>
    public UserChatMessage Message { get; }

    /// <summary>
    /// Gets or sets whether this item steals the next turn join point (<see cref="ChatInputDeliveryMode.Steer"/>)
    /// or waits for the current turn to complete (<see cref="ChatInputDeliveryMode.Queue"/>).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSteer))]
    [NotifyPropertyChangedFor(nameof(IsQueue))]
    public partial ChatInputDeliveryMode DeliveryMode { get; internal set; }

    /// <summary>
    /// Gets the assistant selected when the user submitted this item.
    /// </summary>
    public CustomAssistant? CustomAssistant { get; }

    /// <summary>
    /// Gets the UTC timestamp when this item was enqueued.
    /// </summary>
    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Gets a compact single-line preview suitable for displaying in the pending input tray.
    /// </summary>
    public string PreviewText { get; }

    /// <summary>
    /// Gets the number of attachments carried by <see cref="Message"/>.
    /// </summary>
    public int AttachmentCount => Message.Attachments.Count;

    /// <summary>
    /// Gets whether <see cref="Message"/> carries any attachments.
    /// </summary>
    public bool HasAttachments => Message.Attachments.Count > 0;

    /// <summary>
    /// Gets whether this item carries a user strategy prompt.
    /// </summary>
    public bool HasStrategy => Message is UserStrategyChatMessage;

    /// <summary>
    /// Gets the selected strategy display name when <see cref="HasStrategy"/> is <see langword="true"/>.
    /// </summary>
    public string? StrategyName => (Message as UserStrategyChatMessage)?.Strategy.NameKey.ToString();

    /// <summary>
    /// Gets whether this item is configured to steer the active generation.
    /// </summary>
    public bool IsSteer => DeliveryMode == ChatInputDeliveryMode.Steer;

    /// <summary>
    /// Gets whether this item is queued to run after the active turn finishes.
    /// </summary>
    public bool IsQueue => DeliveryMode == ChatInputDeliveryMode.Queue;

    /// <summary>
    /// Initializes a new pending input item.
    /// </summary>
    public ChatPendingInputItem(
        UserChatMessage message,
        ChatInputDeliveryMode deliveryMode,
        CustomAssistant? customAssistant)
        : this(Guid.NewGuid(), message, deliveryMode, customAssistant, DateTimeOffset.UtcNow)
    {
    }

    internal ChatPendingInputItem(
        Guid id,
        UserChatMessage message,
        ChatInputDeliveryMode deliveryMode,
        CustomAssistant? customAssistant,
        DateTimeOffset createdAt)
    {
        if (deliveryMode is not (ChatInputDeliveryMode.Queue or ChatInputDeliveryMode.Steer))
        {
            throw new ArgumentOutOfRangeException(nameof(deliveryMode), deliveryMode, "Pending inputs must use Queue or Steer delivery.");
        }

        Id = id;
        Message = message;
        DeliveryMode = deliveryMode;
        CustomAssistant = customAssistant;
        CreatedAt = createdAt;
        PreviewText = BuildPreviewText(message);
    }

    private static string BuildPreviewText(UserChatMessage message)
    {
        var raw = message.Content;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return message is UserStrategyChatMessage strategy ? strategy.Strategy.NameKey.ToString() ?? string.Empty : string.Empty;
        }

        var normalized = string.Join(" ", raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 120 ? normalized : string.Concat(normalized.AsSpan(0, 117), "...");
    }
}

/// <summary>
/// Represents a completed background job wake notification waiting to enter the conversation.
/// </summary>
public sealed record ChatBackgroundJobNotification(
    string JobId,
    string ToolName,
    string Label,
    bool IsSuccess,
    string? Output,
    string? ErrorMessage,
    DateTimeOffset CreatedAt);

/// <summary>
/// Thread-safe FIFO buffer for queued/steered user messages and background job wake notifications
/// scoped to a single <see cref="ChatContext"/>.
/// </summary>
public sealed partial class ChatPendingInputQueue : ObservableObject, IDisposable
{
    /// <summary>
    /// Gets the conversation identifier owning this pending input queue.
    /// </summary>
    public Guid ChatId { get; }

    /// <summary>
    /// Gets the UI-thread-observed list of pending user items shown in the tray.
    /// </summary>
    public IReadOnlyBindableList<ChatPendingInputItem> WaitingItems { get; }

    /// <summary>
    /// Gets whether automatic draining of queued user turns is currently paused (typically after Stop).
    /// </summary>
    public bool IsDrainPaused
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _isDrainPaused;
        }
    }

    /// <summary>
    /// Gets the current number of pending user items.
    /// </summary>
    public int WaitingCount
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _userItems.Count;
        }
    }

    /// <summary>
    /// Gets whether at least one pending user item is currently marked as <see cref="ChatInputDeliveryMode.Steer"/>.
    /// </summary>
    public bool HasPendingSteers
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _userItems.Any(static item => item.DeliveryMode == ChatInputDeliveryMode.Steer);
        }
    }

    /// <summary>
    /// Gets whether any completed background job notifications are waiting to be delivered.
    /// </summary>
    public bool HasPendingWakes
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _wakeNotifications.Count > 0;
        }
    }

    /// <summary>
    /// Gets whether there is any eligible turn-level work waiting when an active turn ends.
    /// </summary>
    public bool HasPendingTurnWork
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _wakeNotifications.Count > 0 || (!_isDrainPaused && _userItems.Count > 0);
        }
    }

    /// <summary>
    /// Gets whether pending wakes or non-paused steer messages are ready to inject into the current turn
    /// even while detached background jobs keep that turn open.
    /// </summary>
    public bool HasPendingInTurnWork
    {
        get
        {
            using var scope = _sync.EnterScope();
            return _wakeNotifications.Count > 0 ||
                (!_isDrainPaused && _userItems.Any(static item => item.DeliveryMode == ChatInputDeliveryMode.Steer));
        }
    }

    /// <summary>
    /// Raised when a <see cref="ChatInputDeliveryMode.Steer"/> item is added or promoted.
    /// </summary>
    public event Action? SteerRequested;

    /// <summary>
    /// Raised when a background job completion notification is added.
    /// </summary>
    public event Action? WakeRequested;

    /// <summary>
    /// Raised when user-facing drain state (<see cref="IsDrainPaused"/> or item count) changes.
    /// </summary>
    public event Action? DrainStateChanged;

    /// <summary>
    /// Raised when the persisted pending user items change and should be snapshotted to local storage.
    /// </summary>
    public event EventHandler? PersistedStateChanged;

    private readonly Lock _sync = new();
    private readonly List<ChatPendingInputItem> _userItems = [];
    private readonly Queue<ChatBackgroundJobNotification> _wakeNotifications = [];
    private readonly SourceList<ChatPendingInputItem> _waitingItemsSource = new();
    private readonly CompositeDisposable _disposables = new(2);

    private bool _isDrainPaused;
    private bool _isDisposed;

    /// <summary>
    /// Initializes a new pending input queue.
    /// </summary>
    public ChatPendingInputQueue() : this(Guid.Empty)
    {
    }

    /// <summary>
    /// Initializes a new pending input queue scoped to <paramref name="chatId"/>.
    /// </summary>
    public ChatPendingInputQueue(Guid chatId)
    {
        ChatId = chatId;
        WaitingItems = _waitingItemsSource
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        _waitingItemsSource.DisposeWith(_disposables);
    }

    /// <summary>
    /// Adds a pending user input item. Optionally clears <see cref="IsDrainPaused"/>.
    /// </summary>
    public ChatPendingInputItem Enqueue(
        UserChatMessage message,
        ChatInputDeliveryMode deliveryMode,
        CustomAssistant? customAssistant,
        bool shouldResumeDrain = false)
    {
        message.DeliveryMode = deliveryMode;
        var item = new ChatPendingInputItem(message, deliveryMode, customAssistant);
        bool didPauseChange;
        using (_sync.EnterScope())
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _userItems.Add(item);
            _waitingItemsSource.Add(item);
            didPauseChange = shouldResumeDrain && _isDrainPaused;
            if (didPauseChange)
            {
                _isDrainPaused = false;
            }
        }

        NotifyQueueProperties(didPauseChange, countChanged: true);
        if (deliveryMode == ChatInputDeliveryMode.Steer)
        {
            SteerRequested?.Invoke();
        }
        DrainStateChanged?.Invoke();
        PersistedStateChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    /// <summary>
    /// Enqueues a completed background job notification.
    /// </summary>
    public void EnqueueWakeNotification(ChatBackgroundJobNotification notification)
    {
        using (_sync.EnterScope())
        {
            if (_isDisposed) return;
            _wakeNotifications.Enqueue(notification);
        }

        WakeRequested?.Invoke();
    }

    /// <summary>
    /// Toggles an existing pending user item between <see cref="ChatInputDeliveryMode.Queue"/>
    /// and <see cref="ChatInputDeliveryMode.Steer"/>.
    /// </summary>
    public bool TryToggleDeliveryMode(Guid id, out ChatInputDeliveryMode newMode)
    {
        newMode = default;
        ChatPendingInputItem? target = null;
        using (_sync.EnterScope())
        {
            foreach (var item in _userItems)
            {
                if (item.Id != id) continue;
                newMode = item.DeliveryMode == ChatInputDeliveryMode.Steer
                    ? ChatInputDeliveryMode.Queue
                    : ChatInputDeliveryMode.Steer;
                item.DeliveryMode = newMode;
                item.Message.DeliveryMode = newMode;
                target = item;
                break;
            }

            if (target is null)
            {
                newMode = default;
                return false;
            }
        }

        OnPropertyChanged(nameof(HasPendingSteers));
        if (newMode == ChatInputDeliveryMode.Steer)
        {
            SteerRequested?.Invoke();
        }
        PersistedStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Removes a pending item so the UI can restore it back into the draft composer without disposing its attachments.
    /// </summary>
    public bool TryWithdraw(Guid id, [NotNullWhen(true)] out ChatPendingInputItem? item) =>
        TryRemoveCore(id, out item);

    /// <summary>
    /// Removes a pending item from the queue. Caller owns disposing the returned item's attachments if discarded.
    /// </summary>
    public bool TryRemove(Guid id, [NotNullWhen(true)] out ChatPendingInputItem? item) =>
        TryRemoveCore(id, out item);

    /// <summary>
    /// Atomically drains all currently pending <see cref="ChatInputDeliveryMode.Steer"/> items in FIFO order.
    /// </summary>
    public IReadOnlyList<ChatPendingInputItem> TakeAllSteers()
    {
        List<ChatPendingInputItem>? taken = null;
        using (_sync.EnterScope())
        {
            for (var i = 0; i < _userItems.Count; i++)
            {
                var item = _userItems[i];
                if (item.DeliveryMode != ChatInputDeliveryMode.Steer) continue;
                taken ??= [];
                taken.Add(item);
            }

            if (taken is null) return [];

            _waitingItemsSource.Edit(list =>
            {
                foreach (var item in taken)
                {
                    _userItems.Remove(item);
                    list.Remove(item);
                }
            });
        }

        NotifyQueueProperties(pauseChanged: false, countChanged: true);
        DrainStateChanged?.Invoke();
        PersistedStateChanged?.Invoke(this, EventArgs.Empty);
        return taken;
    }

    /// <summary>
    /// Atomically drains all pending background job completion notifications in FIFO order.
    /// </summary>
    public IReadOnlyList<ChatBackgroundJobNotification> TakeAllWakeNotifications()
    {
        using var scope = _sync.EnterScope();
        if (_wakeNotifications.Count == 0) return [];
        var taken = new List<ChatBackgroundJobNotification>(_wakeNotifications.Count);
        while (_wakeNotifications.TryDequeue(out var item))
        {
            taken.Add(item);
        }

        return taken;
    }

    /// <summary>
    /// Removes and returns the next pending <see cref="ChatInputDeliveryMode.Queue"/> item when draining is not paused.
    /// </summary>
    public bool TryTakeNextQueued([NotNullWhen(true)] out ChatPendingInputItem? item)
    {
        using (_sync.EnterScope())
        {
            if (_isDrainPaused || _userItems.Count == 0)
            {
                item = null;
                return false;
            }

            var index = _userItems.FindIndex(static candidate => candidate.DeliveryMode == ChatInputDeliveryMode.Queue);
            if (index < 0)
            {
                index = 0;
            }

            item = _userItems[index];
            _userItems.RemoveAt(index);
            _waitingItemsSource.Remove(item);
        }

        NotifyQueueProperties(pauseChanged: false, countChanged: true);
        DrainStateChanged?.Invoke();
        PersistedStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>
    /// Pauses automatic draining of queued user items, leaving existing items visible in the tray.
    /// </summary>
    public void PauseDrain()
    {
        bool changed;
        using (_sync.EnterScope())
        {
            if (_userItems.Count == 0 || _isDrainPaused) return;
            _isDrainPaused = true;
            changed = true;
        }

        if (changed)
        {
            NotifyQueueProperties(pauseChanged: true, countChanged: false);
            DrainStateChanged?.Invoke();
        }
    }

    /// <summary>
    /// Resumes automatic draining and returns whether any queued items exist to run.
    /// </summary>
    public bool ResumeDrain()
    {
        bool hasItems;
        bool pauseChanged;
        using (_sync.EnterScope())
        {
            pauseChanged = _isDrainPaused;
            _isDrainPaused = false;
            hasItems = _userItems.Count > 0 || _wakeNotifications.Count > 0;
        }

        if (pauseChanged)
        {
            NotifyQueueProperties(pauseChanged: true, countChanged: false);
            DrainStateChanged?.Invoke();
        }
        return hasItems;
    }

    /// <summary>
    /// Removes all pending user items and returns them to the caller for resource cleanup.
    /// </summary>
    public IReadOnlyList<ChatPendingInputItem> ClearAllUserItems()
    {
        List<ChatPendingInputItem> removed;
        bool pauseChanged;
        using (_sync.EnterScope())
        {
            pauseChanged = _isDrainPaused;
            _isDrainPaused = false;
            if (_userItems.Count == 0)
            {
                if (!pauseChanged) return [];
                removed = [];
            }
            else
            {
                removed = [.. _userItems];
                _userItems.Clear();
                _waitingItemsSource.Clear();
            }
        }

        NotifyQueueProperties(pauseChanged, countChanged: removed.Count > 0);
        DrainStateChanged?.Invoke();
        if (removed.Count > 0)
        {
            PersistedStateChanged?.Invoke(this, EventArgs.Empty);
        }
        return removed;
    }

    internal ChatPendingInputSnapshot[] CaptureSnapshots()
    {
        using var scope = _sync.EnterScope();
        if (_userItems.Count == 0)
        {
            return [];
        }

        var result = new ChatPendingInputSnapshot[_userItems.Count];
        for (var i = 0; i < _userItems.Count; i++)
        {
            var item = _userItems[i];
            var message = item.Message;
            var content = new ChatInputDraftSnapshot(
                message.Content,
                [.. message.Attachments],
                (message as UserStrategyChatMessage)?.Strategy);
            result[i] = new ChatPendingInputSnapshot(
                item.Id,
                content,
                item.DeliveryMode,
                item.CustomAssistant?.Id,
                item.CreatedAt);
        }

        return result;
    }

    internal IReadOnlyList<FileAttachment> CaptureFileAttachments()
    {
        using var scope = _sync.EnterScope();
        if (_userItems.Count == 0)
        {
            return [];
        }

        return _userItems
            .AsValueEnumerable()
            .SelectMany(static item => item.Message.Attachments)
            .OfType<FileAttachment>()
            .DistinctBy(static file => file.Sha256)
            .ToArray();
    }

    internal void Restore(
        IReadOnlyList<ChatPendingInputSnapshot>? snapshots,
        Func<Guid, CustomAssistant?> resolveAssistant,
        bool pauseDrain)
    {
        if (snapshots is not { Count: > 0 })
        {
            return;
        }

        var restored = new List<ChatPendingInputItem>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (snapshot.DeliveryMode is not (ChatInputDeliveryMode.Queue or ChatInputDeliveryMode.Steer))
            {
                continue;
            }

            var attachments = snapshot.Content.Attachments
                .Where(static attachment => attachment is not VisualElementAttachment { IsElementValid: false })
                .ToArray();
            var text = snapshot.Content.Text ?? string.Empty;
            UserChatMessage message = snapshot.Content.Strategy is { } strategy ?
                new UserStrategyChatMessage(text, attachments, strategy) :
                new UserChatMessage(text, attachments);
            message.DeliveryMode = snapshot.DeliveryMode;

            var assistant = snapshot.CustomAssistantId is { } assistantId ? resolveAssistant(assistantId) : null;
            restored.Add(new ChatPendingInputItem(
                snapshot.Id,
                message,
                snapshot.DeliveryMode,
                assistant,
                snapshot.CreatedAt));
        }

        if (restored.Count == 0)
        {
            return;
        }

        bool pauseChanged;
        using (_sync.EnterScope())
        {
            if (_isDisposed) return;
            _userItems.Clear();
            _userItems.AddRange(restored);
            _waitingItemsSource.Edit(list =>
            {
                list.Clear();
                list.AddRange(restored);
            });

            var nextPaused = pauseDrain;
            pauseChanged = _isDrainPaused != nextPaused;
            _isDrainPaused = nextPaused;
        }

        NotifyQueueProperties(pauseChanged, countChanged: true);
        DrainStateChanged?.Invoke();
    }

    /// <summary>
    /// Releases all pending user items and wake notifications when the owning context shuts down.
    /// </summary>
    public IReadOnlyList<ChatPendingInputItem> ReleaseAll()
    {
        List<ChatPendingInputItem> removed;
        using (_sync.EnterScope())
        {
            removed = [.. _userItems];
            _userItems.Clear();
            _wakeNotifications.Clear();
            _isDrainPaused = false;
            _waitingItemsSource.Clear();
        }

        NotifyQueueProperties(pauseChanged: true, countChanged: removed.Count > 0);
        return removed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        using (_sync.EnterScope())
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _userItems.Clear();
            _wakeNotifications.Clear();
        }

        _disposables.Dispose();
    }

    private bool TryRemoveCore(Guid id, [NotNullWhen(true)] out ChatPendingInputItem? item)
    {
        bool pauseChanged;
        using (_sync.EnterScope())
        {
            item = null;
            for (var i = 0; i < _userItems.Count; i++)
            {
                if (_userItems[i].Id != id) continue;
                item = _userItems[i];
                _userItems.RemoveAt(i);
                _waitingItemsSource.Remove(item);
                break;
            }

            if (item is null) return false;
            pauseChanged = _userItems.Count == 0 && _isDrainPaused;
            if (pauseChanged)
            {
                _isDrainPaused = false;
            }
        }

        NotifyQueueProperties(pauseChanged, countChanged: true);
        DrainStateChanged?.Invoke();
        PersistedStateChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private void NotifyQueueProperties(bool pauseChanged, bool countChanged)
    {
        if (pauseChanged) OnPropertyChanged(nameof(IsDrainPaused));
        if (countChanged)
        {
            OnPropertyChanged(nameof(WaitingCount));
            OnPropertyChanged(nameof(HasPendingSteers));
        }
    }
}
