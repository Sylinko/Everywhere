using MessagePack;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

/// <summary>
/// Persists a visual Context boundary in model-visible history without adding a presentation row.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class VisualContextResetChatMessage : ChatMessage
{
    /// <inheritdoc />
    public override AuthorRole Role => new("developer");

    /// <inheritdoc />
    public override bool IsHidden => true;

    /// <summary>Gets the model-facing reset notice.</summary>
    [Key(0)]
    public string Content { get; }

    /// <summary>Gets the identity of the visual Context that became current at this boundary.</summary>
    [Key(1)]
    public Guid VisualContextId { get; }

    [Key(2)]
    public override DateTimeOffset CreatedAt { get; }

    /// <summary>Creates a persistent visual Context boundary.</summary>
    public VisualContextResetChatMessage(string content, Guid visualContextId) : this(content, visualContextId, DateTimeOffset.UtcNow)
    {
    }

    [SerializationConstructor]
    private VisualContextResetChatMessage(string content, Guid visualContextId, DateTimeOffset createdAt)
    {
        Content = content;
        VisualContextId = visualContextId;
        CreatedAt = createdAt;
    }

    /// <inheritdoc />
    public override string ToString() => Content;
}