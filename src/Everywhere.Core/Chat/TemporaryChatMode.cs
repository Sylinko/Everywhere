namespace Everywhere.Chat;

/// <summary>
/// Specifies the mode for temporary chat contexts.
/// </summary>
public enum TemporaryChatMode
{
    [DynamicLocaleKey(LocaleKey.TemporaryChatMode_Never)]
    Never,
    [DynamicLocaleKey(LocaleKey.TemporaryChatMode_RememberLast)]
    RememberLast,
    [DynamicLocaleKey(LocaleKey.TemporaryChatMode_Always)]
    Always
}