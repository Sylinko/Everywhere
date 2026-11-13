namespace Everywhere.Database;

/// <summary>
/// Local-only composer state. It never participates in cloud synchronization.
/// </summary>
public sealed class ChatInputEntity
{
    /// <summary>
    /// Gets or sets the storage slot: Guid.Empty for the one unsent new chat, otherwise ChatId.
    /// </summary>
    public Guid SlotId { get; set; }

    /// <summary>
    /// Gets or sets the real conversation identity. It never changes during first-save handoff.
    /// </summary>
    public Guid ChatId { get; set; }

    /// <summary>
    /// Gets or sets the MessagePack-encoded ordinary draft and optional message edit.
    /// </summary>
    public byte[] Payload { get; set; } = [];
}