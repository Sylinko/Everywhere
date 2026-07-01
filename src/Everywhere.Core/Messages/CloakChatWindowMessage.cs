namespace Everywhere.Messages;

/// <summary>
/// Raise this message to request the chat window to be cloaked (hidden) or un-cloaked (shown).
/// </summary>
/// <param name="IsCloaked"></param>
public sealed record CloakChatWindowMessage(bool IsCloaked);