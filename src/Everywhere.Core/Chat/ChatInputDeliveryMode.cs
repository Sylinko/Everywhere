namespace Everywhere.Chat;

/// <summary>
/// Selects how user input is delivered relative to an active generation turn.
/// </summary>
public enum ChatInputDeliveryMode
{
    /// <summary>
    /// Queue the message to run sequentially after the active turn finishes.
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatBusySubmissionMode_Queue)]
    Queue = 0,

    /// <summary>
    /// Steer the active turn at the next safe boundary (backgrounding supported tools if needed).
    /// </summary>
    [DynamicLocaleKey(LocaleKey.ChatBusySubmissionMode_Steer)]
    Steer = 1
}
