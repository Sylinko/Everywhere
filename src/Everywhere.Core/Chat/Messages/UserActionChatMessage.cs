using CommunityToolkit.Mvvm.ComponentModel;
using Lucide.Avalonia;
using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

/// <summary>
/// Represents a user action message in the chat.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
public sealed partial class UserActionChatMessage : ChatMessage
{
    [IgnoreMember]
    public override AuthorRole Role => new("user");

    [Key(0)]
    [ObservableProperty]
    public partial LucideIconKind Icon { get; set; }

    [Key(1)]
    public IDynamicLocaleKey? HeaderKey { get; }

    /// <summary>
    /// The actual prompt that sends to the LLM.
    /// </summary>
    [Key(2)]
    public string? Content { get; }

    [Key(3)]
    public override DateTimeOffset CreatedAt { get; }

    [SerializationConstructor]
    private UserActionChatMessage(LucideIconKind icon, IDynamicLocaleKey? headerKey, string? content, DateTimeOffset createdAt)
    {
        Icon = icon;
        HeaderKey = headerKey;
        Content = content;
        CreatedAt = createdAt;
    }

    public UserActionChatMessage(LucideIconKind icon, IDynamicLocaleKey? headerKey, string? content)
    {
        Icon = icon;
        HeaderKey = headerKey;
        Content = content;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public override string? ToString() => Content;
}