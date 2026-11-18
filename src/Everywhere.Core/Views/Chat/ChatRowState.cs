using System.Text;
using Everywhere.Chat;

namespace Everywhere.Views;

/// <summary>
/// Captured reasoning values. The source span remains only an identity and action target.
/// </summary>
public sealed record ChatReasoningState(
    string Text,
    string PreviewText,
    long TextVersion,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FinishedAt,
    bool IsRunning)
{
    private const int MaxPreviewLength = 200;

    /// <summary>
    /// Reuses unchanged text while capturing a consistent buffer value and revision when needed.
    /// </summary>
    public static ChatReasoningState Capture(AssistantChatMessage assistant, AssistantChatMessageReasoningSpan span, ChatReasoningState? previous)
    {
        var text = previous?.Text;
        var previewText = previous?.PreviewText;
        var version = previous?.TextVersion;
        if (version != span.ReasoningMarkdownBuilder.Version)
        {
            var snapshot = span.ReasoningMarkdownBuilder.CaptureSnapshot();
            text = snapshot.Text;
            previewText = BuildPreviewText(text);
            version = snapshot.Version;
        }

        return new ChatReasoningState(
            text ?? string.Empty,
            previewText ?? string.Empty,
            version ?? 0,
            span.CreatedAt,
            span.FinishedAt,
            assistant.IsBusy && span.FinishedAt is null);
    }

    private static string BuildPreviewText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxPreviewLength));
        var isPendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (builder.Length > 0)
                {
                    isPendingSpace = true;
                }
                continue;
            }

            if (isPendingSpace)
            {
                if (builder.Length + 1 >= MaxPreviewLength)
                {
                    break;
                }

                builder.Append(' ');
                isPendingSpace = false;
            }

            builder.Append(ch);
            if (builder.Length >= MaxPreviewLength)
            {
                break;
            }
        }

        return builder.ToString();
    }
}