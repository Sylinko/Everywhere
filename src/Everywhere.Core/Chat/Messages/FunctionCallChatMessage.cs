using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [Key(0)]
    public override AuthorRole Role => AuthorRole.Tool;

    [Key(1)]
    [ObservableProperty]
    public partial LucideIconKind Icon { get; set; }

    /// <summary>
    /// Obsolete: Use HeaderKey instead.
    /// </summary>
    [Key(2)]
    private DynamicLocaleKey? ObsoleteHeaderKey
    {
        get => null; // for forward compatibility
        init => HeaderKey = value;
    }

    [Key(3)]
    public string? Content { get; set; }

    [Key(4)]
    [ObservableProperty]
    public partial IDynamicLocaleKey? ErrorMessageKey { get; set; }

    [Key(5)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [Key(6)]
    public List<FunctionCallContent> Calls { get; set; } = [];

    [Key(7)]
    public List<FunctionResultContent> Results { get; set; } = [];

    [Key(8)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    [NotifyPropertyChangedFor(nameof(SerializableDisplayBlocks))] // Notify for serialization purposes
    public partial DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [Key(9)]
    [ObservableProperty]
    public partial IDynamicLocaleKey? HeaderKey { get; set; }

    [Key(10)]
    private IEnumerable<ChatPluginDisplayBlock> SerializableDisplayBlocks
    {
        get => _displaySink.Items;
        init => _displaySink.Reset(value);
    }

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
    public IReadOnlyBindableList<ChatPluginDisplayBlock> DisplayBlocks { get; }

    /// <summary>
    /// The display sink that holds the display blocks for this function call message.
    /// </summary>
    [IgnoreMember]
    public IChatPluginDisplaySink DisplaySink => _displaySink;

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

    // [Key(11)]
    // [ObservableProperty]
    // public partial bool IsExpanded { get; set; } = true;

    [IgnoreMember]
    [JsonIgnore]
    public bool IsWaitingForUserInput =>
        _displaySink.AsValueEnumerable().Any(db => db.IsWaitingForUserInput) ||
        _activityPresentationSlots.AsValueEnumerable().Any(pair => pair.Value.IsWaitingForUserInput);

    /// <summary>
    /// Attachments associated with this action message. Used to provide additional context of a tool call result.
    /// </summary>
    [IgnoreMember]
    public IEnumerable<ChatAttachment> Attachments => Results.Select(r => r.Result).OfType<ChatAttachment>();

    [IgnoreMember] private readonly ChatPluginDisplaySink _displaySink = new();
    [IgnoreMember] private readonly ConcurrentDictionary<string, ActivityPresentationSlot> _activityPresentationSlots = new();
    [IgnoreMember] private readonly CompositeDisposable _disposables = new(3);
    [IgnoreMember] private long _activityPreviewRevision;

    [SerializationConstructor]
    private FunctionCallChatMessage() : this(default, null)
    {
        // This constructor is for the deserializer.
        // The pipeline is set up in the primary constructor.
    }

    public FunctionCallChatMessage(LucideIconKind icon, IDynamicLocaleKey? headerKey)
    {
        Icon = icon;
        HeaderKey = headerKey;

        // Set up the DynamicData pipeline
        DisplayBlocks = _displaySink
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);

        // Monitor IsWaitingForUserInput changes
        _disposables.Add(
            _displaySink
                .Connect()
                .WhenAnyPropertyChanged(nameof(ChatPluginDisplayBlock.IsWaitingForUserInput))
                .Subscribe(_ => OnPropertyChanged(nameof(IsWaitingForUserInput))));

        _disposables.Add(_displaySink);
    }

    /// <summary>
    /// Registers the runtime presentation slot owned by one concrete tool invocation.
    /// </summary>
    /// <remarks>
    /// The slot itself is stable for the invocation lifetime and carries both its latest lightweight
    /// preview and its transient user-input wait count. Updating either value never mutates the
    /// registry, so a late writer cannot accidentally re-register a slot after its invocation has
    /// ended. The concurrent dictionary is needed only for the much rarer registration/removal
    /// operations when multiple tool invocations overlap.
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

    private long NextActivityPreviewRevision() => Interlocked.Increment(ref _activityPreviewRevision);

    private void NotifyActivityPreviewChanged() => OnPropertyChanged(nameof(ActivityPreview));

    private void NotifyUserInputWaitChanged() => OnPropertyChanged(nameof(IsWaitingForUserInput));

    public void Dispose()
    {
        _activityPresentationSlots.Clear();
        _disposables.Dispose();
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
                Interlocked.Exchange(ref _snapshot, new ActivityPreviewSnapshot(value, revision));
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
            if (Interlocked.Increment(ref _userInputWaitCount) == 1)
                owner.NotifyUserInputWaitChanged();
        }

        /// <summary>
        /// Completes one invocation-local user interaction. A count is required because separate
        /// asynchronous operations may overlap even though each operation has a single owner.
        /// </summary>
        public void ExitUserInputWait()
        {
            var count = Interlocked.Decrement(ref _userInputWaitCount);
            Debug.Assert(count >= 0, "User-input wait scopes must be exited exactly once.");
            if (count == 0) owner.NotifyUserInputWaitChanged();
        }
    }

    internal sealed record ActivityPreviewSnapshot(ChatPluginActivityPreview? Preview, long Revision);
}