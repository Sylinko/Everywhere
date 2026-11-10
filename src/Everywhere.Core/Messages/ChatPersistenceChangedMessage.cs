using Everywhere.Chat;

namespace Everywhere.Messages;

/// <summary>
/// Requests persistence after an explicit local change. Publication of loaded or received
/// values never sends this message. The recipient retains the owning runtime for saving.
/// </summary>
public sealed record ChatPersistenceChangedMessage(ChatMetadata Metadata);