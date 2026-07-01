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

/// <summary>Chat context metadata persisted along with the object graph.</summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public partial class ChatContextMetadata(Guid id, DateTimeOffset dateCreated, DateTimeOffset dateModified, string? topic) : ObservableObject
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
            if (SetProperty(ref field, value.SafeSubstring(0, 100))) OnPropertyChanged(nameof(ActualTopic));
        }
    } = topic;

    [IgnoreMember]
    public string? ActualTopic
    {
        get
        {
            if (IsTemporary) return LocaleResolver.ChatContext_Temporary;
            if (string.IsNullOrWhiteSpace(Topic)) return LocaleResolver.ChatContext_Metadata_Topic_Default;
            return Topic;
        }
        private set => Topic = value?.Trim();
    }

    /// <summary>
    /// Indicates whether the topic is being generated.
    /// </summary>
    [IgnoreMember]
    public AtomicBoolean IsGeneratingTopic => new(ref _isGeneratingTopicAtomic);

    [IgnoreMember]
    private int _isGeneratingTopicAtomic;

    [IgnoreMember]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActualTopic))]
    public partial bool IsTemporary { get; set; }

    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsRenaming { get; set; }

    [IgnoreMember]
    [ObservableProperty]
    public partial ChatContextMetadataStates States { get; set; }

    /// <summary>
    /// Indicates whether the context is temporarily deleted and not yet removed from the database.
    /// Used for undo functionality and to prevent immediate hard deletion.
    /// </summary>
    [IgnoreMember]
    public bool IsTemporaryDeleted { get; set; }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // Notify listeners that metadata has changed.
        WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(null, this, e.PropertyName));
    }

    public override bool Equals(object? obj) => obj is ChatContextMetadata other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode();
}