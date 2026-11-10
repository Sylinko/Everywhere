using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Messages;
using MessagePack;

namespace Everywhere.Chat;

[Flags]
public enum ChatContextMetadataStates
{
    None = 0x0,
    Busy = 0x1,
    HasNotification = 0x2
}

/// <summary>
/// Strongly typed runtime metadata. Explicit local edits carry field revisions; installing
/// committed values only publishes property changes and preserves this object's identity.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatMetadata(Guid id, DateTimeOffset dateCreated, DateTimeOffset dateModified, string? topic) : ObservableObject
{
    /// <summary>
    /// Maximum title length for new local edits, shared with the storage model.
    /// </summary>
    public const int MaximumTopicLength = 100;

    /// <summary>
    /// Stable ID (Guid v7) to align with database primary key.
    /// </summary>
    [Key(0)]
    public Guid Id { get; } = id;

    [Key(1)]
    [ObservableProperty]
    public partial DateTimeOffset DateCreated { get; internal set; } = dateCreated;

    /// <summary>
    /// Last durable content activity, used to order history and quota-recovery candidates.
    /// </summary>
    [Key(2)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalDateModified))]
    public partial DateTimeOffset DateModified { get; internal set; } = dateModified;

    [IgnoreMember]
    public DateTime LocalDateModified => DateModified.ToLocalTime().DateTime;

    [Key(3)]
    [field: IgnoreMember]
    public string? Topic
    {
        get;
        internal set
        {
            if (SetProperty(ref field, value))
            {
                OnPropertyChanged(nameof(ActualTopic));
            }
        }
    } = topic;

    [IgnoreMember]
    public string? ActualTopic
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Topic))
            {
                return Topic;
            }

            return IsTemporary ? LocaleKey.ChatContext_Temporary.I18N() : LocaleKey.ChatContext_Metadata_Topic_Default.I18N();
        }
    }

    /// <summary>
    /// Whether this conversation has had a successfully committed database record. Loaded
    /// histories retain this fact even when empty; it does not describe pending edits.
    /// This runtime-only fact never resets when a conversation is deleted.
    /// </summary>
    [IgnoreMember]
    public bool IsPersisted { get; internal set; }

    /// <summary>
    /// Session-only retention; rows may be saved during this run but are removed before the next startup exposes history.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualTopic))]
    public partial bool IsTemporary { get; internal set; }

    [IgnoreMember]
    [ObservableProperty]
    public partial ChatContextMetadataStates States { get; internal set; }

    /// <summary>
    /// Gets the local archive timestamp. Stored in the conversation row, not embedded child payloads.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsArchived))]
    public partial DateTimeOffset? ArchivedAt { get; internal set; }

    /// <summary>
    /// Gets whether this conversation belongs to the archive rather than the active history listing.
    /// </summary>
    [IgnoreMember]
    public bool IsArchived => ArchivedAt.HasValue;

    /// <summary>
    /// Gets whether this conversation leads its history listing. Stored independently of activity and archive state.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsPinned { get; internal set; }

    /// <summary>
    /// Signals an explicit local metadata edit, independently of property publication.
    /// Embedded owners use this to persist serialized child metadata.
    /// </summary>
    public event EventHandler<ChatMetadataField?>? DurableChanged;

    [IgnoreMember] private readonly Dictionary<ChatMetadataField, long> _fieldRevisions = new();
    [IgnoreMember] private long _retentionRevision;
    [IgnoreMember] private long _revision;

    /// <summary>
    /// Renames this conversation without changing its activity timestamp.
    /// Invoke in the root execution domain, or under manager coordination for unloaded history.
    /// </summary>
    public void Rename(string? topic)
    {
        var previous = Topic;
        Topic = topic?.Trim().SafeSubstring(0, MaximumTopicLength).Trim();
        if (Topic != previous)
        {
            MarkChanged(ChatMetadataFields.Topic);
        }
    }

    /// <summary>
    /// Changes local retention. Retaining an already stored temporary chat uses the manager's
    /// commit-before-publication operation instead.
    /// </summary>
    public void SetTemporary(bool isTemporary)
    {
        if (IsTemporary == isTemporary) return;
        IsTemporary = isTemporary;
        _retentionRevision = ++_revision;
        NotifyDurableChanged(null);
    }

    internal void Touch()
    {
        DateModified = DateTimeOffset.UtcNow;
        MarkChanged(ChatMetadataFields.UpdatedAt);
    }

    internal ChatMetadataChange CaptureChanges()
    {
        return new ChatMetadataChange(new Dictionary<ChatMetadataField, long>(_fieldRevisions), _retentionRevision);
    }

    internal void Acknowledge(ChatMetadataChange change)
    {
        foreach (var (field, revision) in change.FieldRevisions)
        {
            if (_fieldRevisions.GetValueOrDefault(field) == revision)
            {
                _fieldRevisions.Remove(field);
            }
        }

        if (_retentionRevision == change.RetentionRevision)
        {
            _retentionRevision = 0;
        }
    }

    private void MarkChanged(ChatMetadataField field)
    {
        _fieldRevisions[field] = ++_revision;
        NotifyDurableChanged(field);
    }

    private void NotifyDurableChanged(ChatMetadataField? field)
    {
        DurableChanged?.Invoke(this, field);
        WeakReferenceMessenger.Default.Send(new ChatPersistenceChangedMessage(this));
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Property publication updates views only. Runtime states publish from their owner.
        if (e.PropertyName != nameof(States))
        {
            WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(this, e.PropertyName));
        }
    }

    public override bool Equals(object? obj) => obj is ChatMetadata other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}

/// <summary>
/// Exact field and local-retention revisions acknowledged only after their captured save commits.
/// </summary>
public sealed record ChatMetadataChange(IReadOnlyDictionary<ChatMetadataField, long> FieldRevisions, long RetentionRevision)
{
    /// <summary>
    /// Whether any local metadata edit still needs persistence.
    /// </summary>
    public bool HasChanges => FieldRevisions.Count != 0 || RetentionRevision != 0;

    /// <summary>
    /// Whether a pending field can request synchronization while content is running.
    /// </summary>
    public bool HasIndependentFields => FieldRevisions.Keys.Any(metadataField => metadataField.IsIndependent);
}