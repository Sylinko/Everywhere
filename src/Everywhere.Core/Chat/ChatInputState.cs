using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Collections;
using Everywhere.StrategyEngine;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Owns one conversation's local input. The ordinary draft remains untouched while a
/// message is edited. After publication, input changes belong exclusively to the UI thread.
/// Input is independent of message persistence, activity timestamps and cloud synchronization.
/// </summary>
public sealed class ChatInputState : ObservableObject, IDisposable
{
    /// <summary>
    /// Gets the conversation whose input is retained locally.
    /// </summary>
    public Guid ChatId { get; }

    /// <summary>
    /// Gets the ordinary, unsent message, including while an edit is active.
    /// </summary>
    public ChatInputDraft Draft { get; private set; } = new();

    /// <summary>
    /// Gets the optional message edit. Its node identity is local to this conversation.
    /// </summary>
    public ChatInputEdit? Edit { get; private set; }

    /// <summary>
    /// Gets the draft displayed by the current conversation's composer.
    /// </summary>
    public ChatInputDraft ActiveDraft => Edit?.Draft ?? Draft;

    /// <summary>
    /// Gets whether any unsent text, attachment, strategy or active edit must be retained.
    /// </summary>
    public bool HasContent => Edit is not null || Draft.HasContent;

    /// <summary>
    /// Signals a local input change. The manager captures it immediately so final saving
    /// never needs to read UI-owned objects after the dispatcher has stopped.
    /// </summary>
    public event EventHandler? Changed;

    public ChatInputState(Guid chatId)
    {
        ChatId = chatId;
        Draft.Changed += HandleDraftChanged;
    }

    /// <summary>
    /// Starts an independent edit without consuming the ordinary draft.
    /// </summary>
    public void BeginEdit(ChatMessageNode node)
    {
        if (node.Message is not UserChatMessage message)
        {
            return;
        }

        EndEdit();
        var draft = new ChatInputDraft
        {
            Text = message.Content,
            SelectedStrategy = message.As<UserStrategyChatMessage>()?.Strategy
        };
        draft.BorrowAttachments(message.Attachments.Where(attachment => attachment is not VisualElementAttachment { IsElementValid: false }));
        draft.Changed += HandleDraftChanged;
        Edit = new ChatInputEdit(node.Id, draft);
        NotifyActiveDraftChanged();
    }

    /// <summary>
    /// Ends an edit after cancellation or accepted submission, revealing the ordinary draft.
    /// Callers remove transferred attachments before completing an accepted submission.
    /// </summary>
    public void EndEdit()
    {
        if (Edit is not { } edit)
        {
            return;
        }
        Edit = null;
        edit.Draft.Changed -= HandleDraftChanged;
        edit.Draft.Dispose();
        NotifyActiveDraftChanged();
    }

    /// <summary>
    /// Captures recoverable input values, not views, live nodes or visual anchor handles.
    /// Empty input removes the local record rather than creating a stored chat. Capture is
    /// synchronous; cancellation belongs to the later storage operation, not this UI snapshot.
    /// </summary>
    public byte[]? Capture()
    {
        return HasContent ?
            MessagePackSerializer.Serialize(
                new ChatInputSnapshot(
                    Draft.Capture(),
                    Edit?.NodeId,
                    Edit?.Draft.Capture()),
                cancellationToken: CancellationToken.None) :
            null;
    }

    /// <summary>
    /// Restores detached local input before the runtime is published. Restoring is not an edit
    /// and does not enqueue another save.
    /// </summary>
    public void Restore(ChatInputSnapshot snapshot)
    {
        Draft.Changed -= HandleDraftChanged;
        Draft.Dispose();

        Draft = ChatInputDraft.Restore(snapshot.Draft);
        Draft.Changed += HandleDraftChanged;

        if (snapshot is { EditingNodeId: { } nodeId, Edit: { } edit })
        {
            var draft = ChatInputDraft.Restore(edit);
            draft.Changed += HandleDraftChanged;
            Edit = new ChatInputEdit(nodeId, draft);
        }
    }

    private void NotifyActiveDraftChanged()
    {
        OnPropertyChanged(nameof(ActiveDraft));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void HandleDraftChanged(object? sender, EventArgs e) => Changed?.Invoke(this, e);

    /// <inheritdoc />
    public void Dispose()
    {
        Draft.Changed -= HandleDraftChanged;
        Draft.Dispose();

        if (Edit is { } edit)
        {
            edit.Draft.Changed -= HandleDraftChanged;
            edit.Draft.Dispose();
        }
    }
}

/// <summary>
/// UI-owned text, strategy and attachment references for one ordinary or editing draft.
/// </summary>
public sealed partial class ChatInputDraft : ObservableObject, IDisposable
{
    /// <summary>
    /// Gets or sets the unsent text.
    /// </summary>
    [ObservableProperty]
    public partial string? Text { get; set; }

    /// <summary>
    /// Gets or sets the strategy selected for this draft.
    /// </summary>
    [ObservableProperty]
    public partial Strategy? SelectedStrategy { get; set; }

    /// <summary>
    /// Gets the mutable, UI-thread-owned attachment collection.
    /// </summary>
    public SourceList<ChatAttachment> AttachmentsSource { get; } = new();

    /// <summary>
    /// Gets the stable bindable projection of this draft's attachments.
    /// </summary>
    public IReadOnlyBindableList<ChatAttachment> Attachments { get; }

    /// <summary>
    /// Gets whether this draft contains user input.
    /// </summary>
    public bool HasContent => !string.IsNullOrEmpty(Text) || SelectedStrategy is not null || AttachmentsSource.Count > 0;

    /// <summary>
    /// Signals changed text, strategy or attachment membership.
    /// </summary>
    public event EventHandler? Changed;

    private readonly IDisposable _binding;
    private readonly IDisposable _changes;
    private IReadOnlyList<ChatAttachment> _borrowedAttachments = [];

    public ChatInputDraft()
    {
        Attachments = AttachmentsSource.Connect().BindEx(out _binding);
        _changes = AttachmentsSource.Connect().Subscribe(_ => Changed?.Invoke(this, EventArgs.Empty));
    }

    partial void OnTextChanged(string? value) => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnSelectedStrategyChanged(Strategy? value) => Changed?.Invoke(this, EventArgs.Empty);

    internal void BorrowAttachments(IEnumerable<ChatAttachment> attachments)
    {
        _borrowedAttachments = attachments.ToArray();
        AttachmentsSource.AddRange(_borrowedAttachments);
    }

    /// <summary>
    /// Removes a draft-owned attachment without disposing a historical attachment borrowed for editing.
    /// </summary>
    public void RemoveAttachment(ChatAttachment attachment)
    {
        AttachmentsSource.Remove(attachment);
        if (!_borrowedAttachments.Contains(attachment) && attachment is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }

    internal ChatInputDraftSnapshot Capture() => new(Text, AttachmentsSource.Items.ToArray(), SelectedStrategy);

    internal static ChatInputDraft Restore(ChatInputDraftSnapshot snapshot)
    {
        var draft = new ChatInputDraft { Text = snapshot.Text, SelectedStrategy = snapshot.Strategy };
        draft.AttachmentsSource.AddRange(
            snapshot.Attachments.Where(attachment => attachment is not VisualElementAttachment { IsElementValid: false }));
        return draft;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _changes.Dispose();
        _binding.Dispose();
        foreach (var attachment in AttachmentsSource.Items.AsValueEnumerable().OfType<IDisposable>())
        {
            if (!_borrowedAttachments.Contains((ChatAttachment)attachment))
            {
                attachment.Dispose();
            }
        }
        AttachmentsSource.Dispose();
    }
}

/// <summary>
/// An edit target and its input, owned by the same conversation rather than by a window.
/// </summary>
public sealed record ChatInputEdit(Guid NodeId, ChatInputDraft Draft);

/// <summary>
/// Local-only recoverable input. This contract never enters chat payloads or sync records.
/// </summary>
[MessagePackObject]
public sealed partial record ChatInputSnapshot(
    [property: Key(0)] ChatInputDraftSnapshot Draft,
    [property: Key(1)] Guid? EditingNodeId,
    [property: Key(2)] ChatInputDraftSnapshot? Edit
);

/// <summary>
/// Serializable input values. Attachment serializers omit live visual anchor handles.
/// </summary>
[MessagePackObject]
public sealed partial record ChatInputDraftSnapshot(
    [property: Key(0)] string? Text,
    [property: Key(1)] ChatAttachment[] Attachments,
    [property: Key(2)] Strategy? Strategy
);