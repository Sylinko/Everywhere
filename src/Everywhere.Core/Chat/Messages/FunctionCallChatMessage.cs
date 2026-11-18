using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Serialization;
using Everywhere.Chat.Plugins;
using Everywhere.Harness;
using Everywhere.I18N;
using Lucide.Avalonia;
using MessagePack;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

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
    public override ChatRole Role => ChatRole.Tool;

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
    public ImmutableArray<ToolCallRecord> Calls => _calls;

    [Key(7)]
    public ImmutableArray<ToolResultRecord> Results => _results;

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
    /// Gets the durable display blocks published by the tool.
    /// </summary>
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

            if (latest?.Preview is { } livePreview)
            {
                return livePreview;
            }

            if (_completedActivityPreview is { } completed)
            {
                return completed;
            }

            if (!IsBusy && _displaySink.Count > 0)
            {
                _completedActivityPreview = DeriveFallbackActivityPreview(_displaySink);
                return _completedActivityPreview;
            }

            return null;
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
    public IEnumerable<ChatAttachment> Attachments => Results.SelectMany(GetAttachments);

    private static IEnumerable<ChatAttachment> GetAttachments(ToolResultRecord result)
    {
        if (result.Result is ChatAttachment attachment) yield return attachment;
        if (result.Result is ChatFunctionResult structured)
        {
            foreach (var item in structured.Attachments)
            {
                yield return item;
            }
        }
        foreach (var file in result.Files)
        {
            yield return file.Attachment;
        }
    }

    [IgnoreMember] private readonly ChatPluginDisplaySink _displaySink = new();
    [IgnoreMember] private readonly ConcurrentDictionary<string, ActivityPresentationSlot> _activityPresentationSlots = new();
    [IgnoreMember] private ChatPluginActivityPreview? _completedActivityPreview;

    [IgnoreMember] private ImmutableArray<ToolCallRecord> _calls = [];
    [IgnoreMember] private ImmutableArray<ToolResultRecord> _results = [];
    [IgnoreMember] private long _activityPreviewRevision;

    [SerializationConstructor]
    private FunctionCallChatMessage(
        LucideIconKind icon,
        DynamicLocaleKey? legacyHeaderKey,
        string? content,
        IDynamicLocaleKey? errorMessageKey,
        DateTimeOffset createdAt,
        ImmutableArray<ToolCallRecord> calls,
        ImmutableArray<ToolResultRecord> results,
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
        var imageViews = default(List<ViewImageFunctionResult>);
        var viewedCount = 0;
        foreach (var result in Results)
        {
            if (result.Result is not ViewImageFunctionResult images)
            {
                continue;
            }

            imageViews ??= [];
            imageViews.Add(images);
            viewedCount += images.ViewedCount;
        }

        var headerKey = imageViews is not null && !isRunning ?
            new FormattedDynamicLocaleKey(LocaleKey.BuiltInChatPlugin_Essential_ViewImage_ViewedCount, new DirectLocaleKey(viewedCount)) :
            HeaderKey;
        return new ChatFunctionState(
            Icon,
            headerKey,
            Content,
            ActivityPreview,
            ErrorMessageKey,
            CreatedAt,
            isRunning ? null : FinishedAt,
            isRunning,
            IsWaitingForUserInput,
            DisplayBlocks,
            imageViews is null ? Array.Empty<ViewImageFunctionResult>() : imageViews);
    }

    private void HandleDisplayBlocksChanged(object? sender, EventArgs e) => NotifyChanged();

    /// <inheritdoc />
    protected override void OnOwnerChanged(ChatMessageNode? owner) => _displaySink.SetOwner(owner);

    /// <summary>
    /// Adds a function call and notifies owners that the serialized call list changed.
    /// Keeping the mutation here makes in-progress tool calls visible to persistence before
    /// the enclosing function-call message finishes.
    /// </summary>
    public void AddCall(ToolCallRecord call)
    {
        ImmutableInterlocked.InterlockedExchange(ref _calls, _calls.Add(call));
        NotifyChanged();
    }

    /// <summary>
    /// Adds a function result and notifies owners that the serialized result list changed.
    /// </summary>
    public void AddResult(ToolResultRecord result)
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
        if (slot.Snapshot.Preview is { } lastPreview)
        {
            _completedActivityPreview = lastPreview;
        }
        if (!_activityPresentationSlots.TryRemove(invocationId, out _)) return;
        NotifyActivityPreviewChanged();
        if (slot.IsWaitingForUserInput) NotifyUserInputWaitChanged();
    }

    private static ChatPluginActivityPreview? DeriveFallbackActivityPreview(IEnumerable<ChatPluginDisplayBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case ChatPluginFileReferencesDisplayBlock { References.Count: > 0 } refs:
                    return new ChatPluginFileReferencesActivityPreview(refs.References);
                case ChatPluginFileDifferenceDisplayBlock { FilePath.Length: > 0 } diff:
                    return new ChatPluginFileReferencesActivityPreview([new ChatPluginFileReference(diff.FilePath)]);
                case ChatPluginTerminalDisplayBlock { Command.Length: > 0 } terminal:
                    return new ChatPluginCommandActivityPreview(terminal.Command);
                case ChatPluginUrlsDisplayBlock { Urls.Count: > 0 } urls:
                {
                    var uris = new List<Uri>(urls.Urls.Count);
                    foreach (var url in urls.Urls)
                    {
                        if (Uri.TryCreate(url.Url, UriKind.Absolute, out var uri))
                        {
                            uris.Add(uri);
                        }
                    }

                    if (uris.Count > 0)
                    {
                        return new ChatPluginUrlsActivityPreview(uris);
                    }

                    return new ChatPluginTextActivityPreview(urls.Urls[0].DisplayNameKey);
                }
                case ChatPluginSubagentDisplayBlock subagent:
                    return new ChatPluginSubagentActivityPreview(subagent.ChatData);
                case ChatPluginCodeBlockDisplayBlock { Code.Length: > 0 } code:
                {
                    var line = NormalizeSingleLine(code.Code, 160);
                    if (line.Length > 0)
                    {
                        return new ChatPluginTextActivityPreview(new DirectLocaleKey(line));
                    }
                    break;
                }
                case ChatPluginContainerDisplayBlock container:
                {
                    var nested = DeriveFallbackActivityPreview(container.Children);
                    if (nested is not null)
                    {
                        return nested;
                    }
                    break;
                }
            }
        }

        return null;
    }

    private static string NormalizeSingleLine(string text, int maxLength)
    {
        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        var isPendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0)
                {
                    isPendingSpace = true;
                }
                continue;
            }

            if (isPendingSpace)
            {
                if (builder.Length + 1 >= maxLength)
                {
                    break;
                }

                builder.Append(' ');
                isPendingSpace = false;
            }

            builder.Append(ch);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString();
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