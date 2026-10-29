namespace Everywhere.Chat;

/// <summary>
/// Selects a history listing without changing a conversation's retention or pinning.
/// </summary>
public enum ChatHistoryView
{
    [DynamicLocaleKey(LocaleKey.ChatHistory_Recent)]
    Recent,
    [DynamicLocaleKey(LocaleKey.ChatHistory_Temporary)]
    Temporary,
    [DynamicLocaleKey(LocaleKey.ChatHistory_Archived)]
    Archived
}