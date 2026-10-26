using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json.Serialization;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Common;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

/// <summary>
/// Represents a function call action message in the chat.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class FunctionCallChatMessage : ChatMessage, IHaveChatAttachments, IDisposable
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.FunctionCall;

    [IgnoreMember]
    public override AuthorRole Role => AuthorRole.Tool;

    [Key(1)]
    public LucideIconKind Icon
    {
        get;
        set => SetProperty(ref field, value);
    }

    /// <summary>
    /// Obsolete: Use HeaderKey instead.
    /// </summary>
    [Key(2)]
#pragma warning disable CA1822
    // ReSharper disable once MemberCanBeMadeStatic.Local
    // for forward compatibility
    private DynamicLocaleKey? LegacyHeaderKey => null;
#pragma warning restore CA1822

    [Key(3)]
    public string? Content
    {
        get;
        set => SetProperty(ref field, value);
    }

    [Key(4)]
    public IDynamicLocaleKey? ErrorMessageKey
    {
        get;
        set => SetProperty(ref field, value);
    }

    [Key(5)]
    public override DateTimeOffset CreatedAt { get; }

    [Key(6)]
    public ImmutableArray<FunctionCallContent> Calls => _calls;

    [Key(7)]
    public ImmutableArray<FunctionResultContent> Results => _results;

    [Key(8)]
    public DateTimeOffset FinishedAt
    {
        get;
        set => SetProperty(ref field, value);
    } = DateTimeOffset.UtcNow;

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [Key(9)]
    public IDynamicLocaleKey? HeaderKey
    {
        get;
        set => SetProperty(ref field, value);
    }

    [Key(10)]
    private IEnumerable<ChatPluginDisplayBlock> SerializableDisplayBlocks => _displaySink.Items;

    /// <summary>
    /// The display blocks that make up the content of this function call message,
    /// which can include text, markdown, progress indicators, file references, and function call/result displays.
    /// These blocks are rendered in the chat UI to present the function call information to the user.
    /// And can be serialized for persistence or transmission.
    /// </summary>
    /// <remarks>
    /// The reason why we need to populate the Content property of function call/result display blocks
    /// is that during deserialization, the references to the actual FunctionCallContent and FunctionResultContent
    /// objects are not automatically restored. Therefore, we need to manually link them back
    /// based on their IDs after deserialization. This ensures that the display blocks have access
    /// to the full details of the function calls and results they are meant to represent.
    /// </remarks>
    [IgnoreMember]
    public IReadOnlyList<ChatPluginDisplayBlock> DisplayBlocks => _displaySink.Items;

    /// <summary>
    /// The display sink that holds the display blocks for this function call message.
    /// </summary>
    [IgnoreMember]
    public ChatPluginDisplaySink DisplaySink => _displaySink;

    /// <summary>
    /// Gets the most recently updated preview among the tool invocations that are still active in
    /// this function-call message.
    /// </summary>
    /// <remarks>
    /// Presentation slots are runtime-only and are never serialized. Each invocation writes only to its
    /// own slot; this aggregate getter is read by the presentation layer and therefore does not
    /// expose registration or cleanup operations to plugins.
    /// </remarks>
    [IgnoreMember]
    [JsonIgnore]
    public ChatPluginActivityPreview? ActivityPreview
    {
        get
        {
            ActivityPreviewSnapshot? latest = null;
            foreach (var pair in _activityPresentationSlots)
            {
                var snapshot = pair.Value.Snapshot;
                if (snapshot.Preview is null || latest is not null && snapshot.Revision <= latest.Revision) continue;
                latest = snapshot;
            }

            return latest?.Preview;
        }
    }

    [IgnoreMember]
    [JsonIgnore]
    public bool IsWaitingForUserInput =>
        _activityPresentationSlots.AsValueEnumerable().Any(pair => pair.Value.IsWaitingForUserInput);

    /// <summary>
    /// Attachments associated with this action message. Used to provide additional context of a tool call result.
    /// </summary>
    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => Results.SelectMany(static result => result.Result switch
    {
        ChatAttachment attachment => [attachment],
        ChatFunctionResult structuredResult => structuredResult.Attachments,
        _ => []
    });

    [IgnoreMember] private readonly ChatPluginDisplaySink _displaySink = new();
    [IgnoreMember] private readonly ConcurrentDictionary<string, ActivityPresentationSlot> _activityPresentationSlots = new();

    [IgnoreMember] private ImmutableArray<FunctionCallContent> _calls = [];
    [IgnoreMember] private ImmutableArray<FunctionResultContent> _results = [];
    [IgnoreMember] private long _activityPreviewRevision;

    [SerializationConstructor]
    private FunctionCallChatMessage(
        LucideIconKind icon,
        DynamicLocaleKey? legacyHeaderKey,
        string? content,
        IDynamicLocaleKey? errorMessageKey,
        DateTimeOffset createdAt,
        ImmutableArray<FunctionCallContent> calls,
        ImmutableArray<FunctionResultContent> results,
        DateTimeOffset finishedAt,
        IDynamicLocaleKey? headerKey,
        IEnumerable<ChatPluginDisplayBlock>? displayBlocks
    ) : this()
    {
        Icon = icon;
        HeaderKey = headerKey ?? legacyHeaderKey;
        Content = content;
        ErrorMessageKey = errorMessageKey;
        CreatedAt = createdAt;
        _calls = calls;
        _results = results;
        FinishedAt = finishedAt;

        // Populate the display sink with the deserialized blocks
        if (displayBlocks is not null) _displaySink.AppendBlocks(displayBlocks);
    }

    public FunctionCallChatMessage(LucideIconKind icon, IDynamicLocaleKey? headerKey) : this()
    {
        Icon = icon;
        HeaderKey = headerKey;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private FunctionCallChatMessage()
    {
        _displaySink.Changed += HandleDisplayBlocksChanged;
    }

    /// <inheritdoc />
    public override ChatFunctionState Capture()
    {
        var isRunning = IsBusy;
        return new ChatFunctionState(
            Icon,
            HeaderKey,
            Content,
            ActivityPreview,
            ErrorMessageKey,
            CreatedAt,
            isRunning ? null : FinishedAt,
            isRunning,
            IsWaitingForUserInput,
            DisplayBlocks);
    }

    private void HandleDisplayBlocksChanged(object? sender, EventArgs e) => NotifyChanged();

    /// <inheritdoc />
    protected override void OnOwnerChanged(ChatMessageNode? owner) => _displaySink.SetOwner(owner);

    /// <summary>
    /// Adds a function call and notifies owners that the serialized call list changed.
    /// Keeping the mutation here makes in-progress tool calls visible to persistence before
    /// the enclosing function-call message finishes.
    /// </summary>
    public void AddCall(FunctionCallContent call)
    {
        ImmutableInterlocked.InterlockedExchange(ref _calls, _calls.Add(call));
        NotifyChanged();
    }

    /// <summary>
    /// Adds a function result and notifies owners that the serialized result list changed.
    /// </summary>
    public void AddResult(FunctionResultContent result)
    {
        ImmutableInterlocked.InterlockedExchange(ref _results, _results.Add(result));
        NotifyChanged();
    }

    /// <summary>
    /// Registers the runtime presentation slot owned by one concrete tool invocation.
    /// </summary>
    /// <remarks>
    /// The slot itself is stable for the invocation lifetime and carries both its latest lightweight
    /// preview and its transient user-input wait count. Updating either value never mutates the
    /// registry, so a late writer cannot accidentally re-register a slot after its invocation has
    /// ended. Runtime registration and cleanup use the root execution domain; the concurrent
    /// dictionary lets the UI enumerate those slots without synchronizing its snapshot reads.
    /// </remarks>
    internal ActivityPresentationSlot RegisterActivityPresentation(string invocationId)
    {
        var slot = new ActivityPresentationSlot(this);
        if (!_activityPresentationSlots.TryAdd(invocationId, slot))
            throw new InvalidOperationException($"Activity presentation state is already registered for invocation '{invocationId}'.");

        return slot;
    }

    /// <summary>
    /// Removes an invocation's complete presentation slot. Remaining invocations are left untouched;
    /// the aggregate getters naturally fall back to the latest remaining preview and wait state.
    /// </summary>
    internal void UnregisterActivityPresentation(string invocationId, ActivityPresentationSlot slot)
    {
        if (!_activityPresentationSlots.TryGetValue(invocationId, out var registered) || !ReferenceEquals(registered, slot)) return;
        if (!_activityPresentationSlots.TryRemove(invocationId, out _)) return;
        NotifyActivityPreviewChanged();
        if (slot.IsWaitingForUserInput) NotifyUserInputWaitChanged();
    }

    private long NextActivityPreviewRevision() => ++_activityPreviewRevision;

    private void NotifyActivityPreviewChanged() => NotifyChanged(isDurable: false);

    private void NotifyUserInputWaitChanged() => NotifyChanged(isDurable: false);

    public void Dispose()
    {
        _activityPresentationSlots.Clear();
        _displaySink.Changed -= HandleDisplayBlocksChanged;
        _displaySink.Dispose();
    }

    /// <summary>
    /// Stores transient presentation state for one invocation. Preview replacement and wait-count
    /// transitions are independent atomic operations: plugins have one logical preview writer, but
    /// one invocation may have overlapping user interactions and therefore uses a counter rather
    /// than a Boolean flag.
    /// </summary>
    internal sealed class ActivityPresentationSlot(FunctionCallChatMessage owner)
    {
        private ActivityPreviewSnapshot _snapshot = new(null, 0);
        private int _userInputWaitCount;

        public ChatPluginActivityPreview? Preview
        {
            get => Volatile.Read(ref _snapshot).Preview;
            set
            {
                var revision = value is null ? 0 : owner.NextActivityPreviewRevision();
                Volatile.Write(ref _snapshot, new ActivityPreviewSnapshot(value, revision));
                owner.NotifyActivityPreviewChanged();
            }
        }

        /// <summary>
        /// Gets whether at least one interaction owned by this invocation is waiting for the user.
        /// </summary>
        public bool IsWaitingForUserInput => Volatile.Read(ref _userInputWaitCount) > 0;

        public ActivityPreviewSnapshot Snapshot => Volatile.Read(ref _snapshot);

        /// <summary>
        /// Starts one invocation-local user interaction. Only the zero-to-one transition changes
        /// the aggregate property, avoiding redundant presentation refreshes for overlapping waits.
        /// </summary>
        public void EnterUserInputWait()
        {
            Volatile.Write(ref _userInputWaitCount, _userInputWaitCount + 1);
            if (_userInputWaitCount == 1)
            {
                owner.NotifyUserInputWaitChanged();
            }
        }

        /// <summary>
        /// Completes one invocation-local user interaction. A count is required because separate
        /// asynchronous operations may overlap even though each operation has a single owner.
        /// </summary>
        public void ExitUserInputWait()
        {
            var count = _userInputWaitCount - 1;
            Volatile.Write(ref _userInputWaitCount, count);
            Debug.Assert(count >= 0, "User-input wait scopes must be exited exactly once.");
            if (count == 0)
            {
                owner.NotifyUserInputWaitChanged();
            }
        }
    }

    internal sealed record ActivityPreviewSnapshot(ChatPluginActivityPreview? Preview, long Revision);
}