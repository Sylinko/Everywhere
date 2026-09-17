using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [IgnoreMember]
    public override AuthorRole Role => new("action");

    [Key(1)]
    [ObservableProperty]
    public partial LucideIconKind Icon { get; set; }

    [Key(2)]
    [ObservableProperty]
    public partial DynamicLocaleKey? HeaderKey { get; set; }

    [Key(3)]
    [ObservableProperty]
    public partial string? Content { get; set; }

    [Key(4)]
    [ObservableProperty]
    public partial IDynamicLocaleKey? ErrorMessageKey { get; set; }

    [Key(5)]
    public override DateTimeOffset CreatedAt { get; }

    [Key(6)]
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ElapsedSeconds))]
    public partial DateTimeOffset FinishedAt { get; set; }

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
}