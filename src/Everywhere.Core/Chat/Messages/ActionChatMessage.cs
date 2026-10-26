using System.Text.Json.Serialization;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

/// <summary>
/// Represents an action message in the chat.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class ActionChatMessage : ChatMessage
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.Action;

    [IgnoreMember]
    public override AuthorRole Role => new("action");

    [Key(1)]
    public LucideIconKind Icon
    {
        get;
        set => SetProperty(ref field, value);
    }

    [Key(2)]
    public DynamicLocaleKey? HeaderKey
    {
        get;
        set => SetProperty(ref field, value);
    }

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
    public DateTimeOffset FinishedAt
    {
        get;
        set => SetProperty(ref field, value);
    }

    [IgnoreMember]
    [JsonIgnore]
    public double ElapsedSeconds => Math.Max((FinishedAt - CreatedAt).TotalSeconds, 0);

    [SerializationConstructor]
    private ActionChatMessage(
        LucideIconKind icon,
        DynamicLocaleKey? headerKey,
        string? content,
        IDynamicLocaleKey? errorMessageKey,
        DateTimeOffset createdAt,
        DateTimeOffset finishedAt)
    {
        Icon = icon;
        HeaderKey = headerKey;
        Content = content;
        ErrorMessageKey = errorMessageKey;
        CreatedAt = createdAt;
        FinishedAt = finishedAt;
    }

    public ActionChatMessage(LucideIconKind icon, DynamicLocaleKey? headerKey)
    {
        Icon = icon;
        HeaderKey = headerKey;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public override ChatActionState Capture()
    {
        return new ChatActionState(Icon, HeaderKey, ErrorMessageKey, IsBusy, ElapsedSeconds);
    }
}