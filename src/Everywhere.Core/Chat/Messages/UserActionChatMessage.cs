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
    [ObservableProperty]
    public partial DynamicLocaleKey? HeaderKey { get; set; }

    /// <summary>
    /// The actual prompt that sends to the LLM.
    /// </summary>
    [Key(2)]
    [ObservableProperty]
    public partial string? Content { get; set; }

    [SerializationConstructor]
    private UserActionChatMessage() { }

    public UserActionChatMessage(LucideIconKind icon, DynamicLocaleKey? headerKey, string? content)
    {
        Icon = icon;
        HeaderKey = headerKey;
        Content = content;
    }

    public override string? ToString() => Content;
}