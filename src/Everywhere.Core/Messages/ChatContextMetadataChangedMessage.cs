using Everywhere.Chat;

namespace Everywhere.Messages;

/// <summary>
/// Message sent when conversation metadata changes.
/// </summary>
/// <param name="metadata">The metadata that has changed.</param>
/// <param name="propertyName">
/// DateModified -> refreshes history activity and ordering.
/// Topic -> refreshes the displayed title.
/// States -> indicates runtime activity or notification state has changed.
/// </param>
public class ChatContextMetadataChangedMessage(ChatMetadata metadata, string? propertyName)
{
    public ChatMetadata Metadata { get; set; } = metadata;

    public string? PropertyName { get; set; } = propertyName;
}