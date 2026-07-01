using Everywhere.Chat;

namespace Everywhere.Messages;

/// <summary>
/// Message sent when chat context metadata changes.
/// </summary>
/// <param name="context">The chat context whose metadata has changed. Null if the context has been released.</param>
/// <param name="metadata">The metadata that has changed.</param>
/// <param name="propertyName">
/// DateModified -> indicates the context has been modified. Need to save.
/// Topic -> indicates the topic has changed. Need to save.
/// IsSelected -> indicates selection state has changed.
/// </param>
public class ChatContextMetadataChangedMessage(ChatContext? context, ChatContextMetadata metadata, string? propertyName)
{
    public ChatContext? Context { get; set; } = context;

    public ChatContextMetadata Metadata { get; set; } = metadata;

    public string? PropertyName { get; set; } = propertyName;
}