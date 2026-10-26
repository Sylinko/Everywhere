using Lucide.Avalonia;

namespace Everywhere.Chat;

/// <summary>
/// A turn-owned, non-persisted operation. It never creates or retains presentation objects.
/// Mutation and completion use the root execution domain; UI readers capture immutable state.
/// </summary>
public sealed class ChatBusyActivity : IBusyActivity
{
    /// <inheritdoc />
    public LucideIconKind Icon
    {
        get => State.Icon;
        set => Update(state => state with { Icon = value });
    }

    /// <inheritdoc />
    public IDynamicLocaleKey HeaderKey
    {
        get => State.HeaderKey;
        set => Update(state => state with { HeaderKey = value });
    }

    /// <inheritdoc />
    public IDynamicLocaleKey? SecondaryHeaderKey
    {
        get => State.SecondaryHeaderKey;
        set => Update(state => state with { SecondaryHeaderKey = value });
    }

    /// <inheritdoc />
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public DateTimeOffset? FinishedAt => State.FinishedAt;

    /// <inheritdoc />
    public bool IsRunning => FinishedAt is null;

    /// <summary>
    /// Gets the fixed placement owner. A null owner represents an unattached operation.
    /// </summary>
    public ChatMessageNode? Owner { get; }

    /// <summary>
    /// Gets the last assistant span present when the operation began.
    /// </summary>
    public AssistantChatMessageSpan? AnchorSpan { get; }

    /// <summary>
    /// Gets whether completion removes the operation from the runtime chronology.
    /// </summary>
    public bool RemoveAfterCompletion { get; }

    /// <summary>
    /// Gets the current immutable state of the operation. This is a snapshot of the last mutation; it may be stale if the owner has completed or disposed the turn.
    /// </summary>
    public ChatBusyActivityState State => Volatile.Read(ref _state);

    private ChatBusyActivityState _state;

    internal ChatBusyActivity(
        ChatMessageNode? owner,
        LucideIconKind icon,
        IDynamicLocaleKey headerKey,
        IDynamicLocaleKey? secondaryHeaderKey,
        bool removeAfterCompletion)
    {
        Owner = owner;
        AnchorSpan = owner is { IsMessageLoaded: true, Message: AssistantChatMessage assistant } ? assistant.Items.LastOrDefault() : null;
        RemoveAfterCompletion = removeAfterCompletion;
        _state = new ChatBusyActivityState(icon, headerKey, secondaryHeaderKey, null);
    }

    private void Update(Func<ChatBusyActivityState, ChatBusyActivityState> update)
    {
        if (_state.FinishedAt is null)
        {
            Volatile.Write(ref _state, update(_state));
            Owner?.Turn?.Invalidate();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_state.FinishedAt is null)
        {
            Volatile.Write(ref _state, _state with { FinishedAt = DateTimeOffset.UtcNow });
            Owner?.Turn?.CompleteActivity(this);
        }
    }
}

/// <summary>
/// Captured values for a runtime activity; source identity and placement belong to its owner.
/// </summary>
public sealed record ChatBusyActivityState(
    LucideIconKind Icon,
    IDynamicLocaleKey HeaderKey,
    IDynamicLocaleKey? SecondaryHeaderKey,
    DateTimeOffset? FinishedAt
);