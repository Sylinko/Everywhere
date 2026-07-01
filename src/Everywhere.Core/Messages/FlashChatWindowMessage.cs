namespace Everywhere.Messages;

/// <summary>
/// Flash the chat window to get the user's attention. The window will flash the taskbar icon if not focused.
/// The optional prompt will show as a system notification if the window is not visible.
/// </summary>
/// <param name="Prompt"></param>
public sealed record FlashChatWindowMessage(string? Prompt);