namespace Everywhere.Chat;

/// <summary>Actual final generation output and terminal cause, including compression-driven message changes.</summary>
public sealed record ChatGenerationResult(AssistantChatMessage Message, Exception? Error)
{
    /// <summary>Gets the available formal output, including useful partial text after terminal failure.</summary>
    public string Output => Message.ToString();
}