namespace Everywhere.Chat;

/// <summary>
/// Specifies how conversation turn navigation is presented.
/// </summary>
public enum ChatTurnNavigationMode
{
    [DynamicLocaleKey(LocaleKey.ChatTurnNavigationMode_None)]
    None,

    [DynamicLocaleKey(LocaleKey.ChatTurnNavigationMode_Simple)]
    Simple,

    [DynamicLocaleKey(LocaleKey.ChatTurnNavigationMode_Fluid)]
    Fluid
}