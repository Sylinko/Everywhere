using Everywhere.Chat;

namespace Everywhere.Views;

/// <summary>
/// Captured reasoning values. The source span remains only an identity and action target.
/// </summary>
public sealed record ChatReasoningState(string Text, long TextVersion, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt, bool IsRunning)
{
    /// <summary>
    /// Reuses unchanged text while capturing a consistent buffer value and revision when needed.
    /// </summary>
    public static ChatReasoningState Capture(AssistantChatMessage assistant, AssistantChatMessageReasoningSpan span, ChatReasoningState? previous)
    {
        var text = previous?.Text;
        var version = previous?.TextVersion;
        if (version != span.ReasoningMarkdownBuilder.Version)
        {
            var snapshot = span.ReasoningMarkdownBuilder.CaptureSnapshot();
            text = snapshot.Text;
            version = snapshot.Version;
        }

        return new ChatReasoningState(
            text ?? string.Empty,
            version ?? 0,
            span.CreatedAt,
            span.FinishedAt,
            assistant.IsBusy && span.FinishedAt is null);
    }
}