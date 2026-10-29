using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Messages;
using Everywhere.Utilities;
using MessagePack;

namespace Everywhere.Chat;

[Flags]
public enum ChatContextMetadataStates
{
    None = 0x0,
    Busy = 0x1,
    HasNotification = 0x2
}

/// <summary>Chat data metadata persisted along with the object graph.</summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatMetadata(Guid id, DateTimeOffset dateCreated, DateTimeOffset dateModified, string? topic) : ObservableObject
{
    /// <summary>
    /// Stable ID (Guid v7) to align with database primary key.
    /// </summary>
    [Key(0)]
    public Guid Id { get; } = id;

    [Key(1)]
    public DateTimeOffset DateCreated { get; } = dateCreated;

    [Key(2)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LocalDateModified))]
    public partial DateTimeOffset DateModified { get; set; } = dateModified;

    [IgnoreMember]
    public DateTime LocalDateModified => DateModified.ToLocalTime().DateTime;

    [Key(3)]
    [field: IgnoreMember]
    public string? Topic
    {
        get;
        set
        {
            if (SetProperty(ref field, value?.Trim().SafeSubstring(0, 100).Trim())) OnPropertyChanged(nameof(ActualTopic));
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

    [IgnoreMember]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualTopic))]
    public partial bool IsTemporary { get; set; }

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

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Data changes publish after dirty tracking, and runtime states publish from their owner.
        // Keep one notification per change rather than repeating it here.
        if (e.PropertyName is not (nameof(DateModified) or nameof(States)))
        {
            WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(this, e.PropertyName));
        }
    }

    public override bool Equals(object? obj) => obj is ChatMetadata other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}