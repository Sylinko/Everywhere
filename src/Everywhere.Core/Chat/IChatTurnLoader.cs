namespace Everywhere.Chat;

/// <summary>
/// Reads detached whole-turn message bodies; the runtime installs them into existing node identities.
/// A null turn identifies pre-user auxiliary nodes, including the root.
/// </summary>
public interface IChatTurnLoader
{
    /// <summary>
    /// Reads stored bodies as detached objects. The runtime installs only bodies still missing from its graph.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ChatMessage>> LoadMessagesAsync(Guid chatId, Guid? turnId, CancellationToken cancellationToken);

    /// <summary>
    /// Reads one covered user message's text without loading its turn's assistant bodies.
    /// </summary>
    Task<string> ReadUserTextAsync(Guid chatId, Guid nodeId, ChatMessageKind kind, CancellationToken cancellationToken);
}