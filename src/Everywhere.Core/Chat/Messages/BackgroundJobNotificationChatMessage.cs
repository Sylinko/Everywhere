using MessagePack;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Everywhere.Chat;

/// <summary>
/// Persists a completed or failed background-tool notification in conversation history, rendering
/// as a lightweight expandable event strip in the timeline while delivering full results to the model.
/// </summary>
[MessagePackObject(AllowPrivate = true, OnlyIncludeKeyedMembers = true)]
[method: SerializationConstructor]
public sealed partial class BackgroundJobNotificationChatMessage(
    string jobId,
    string toolName,
    string label,
    bool isSuccess,
    string output,
    string? errorMessage,
    DateTimeOffset createdAt
) : ChatMessage
{
    /// <inheritdoc />
    public override ChatMessageKind Kind => ChatMessageKind.BackgroundJobNotification;

    /// <inheritdoc />
    [IgnoreMember]
    public override ChatRole Role => new("user");

    /// <summary>
    /// Gets the background job identifier returned to the model when the tool was detached.
    /// </summary>
    [Key(0)]
    public string JobId { get; } = jobId;

    /// <summary>
    /// Gets the model-exposed tool name that originated this background job.
    /// </summary>
    [Key(1)]
    public string ToolName { get; } = toolName;

    /// <summary>
    /// Gets a concise user-facing label (such as the command line or subagent title).
    /// </summary>
    [Key(2)]
    public string Label { get; } = label;

    /// <summary>
    /// Gets whether the background job finished without an unhandled failure.
    /// </summary>
    [Key(3)]
    public bool IsSuccess { get; } = isSuccess;

    /// <summary>
    /// Gets the captured output or status summary produced by the completed background job.
    /// </summary>
    [Key(4)]
    public string Output { get; } = output;

    /// <summary>
    /// Gets the failure reason when <see cref="IsSuccess"/> is <see langword="false"/>.
    /// </summary>
    [Key(5)]
    public string? ErrorMessage { get; } = errorMessage;

    /// <inheritdoc />
    [Key(6)]
    public override DateTimeOffset CreatedAt { get; } = createdAt;

    /// <summary>
    /// Creates a new background job completion notification.
    /// </summary>
    public BackgroundJobNotificationChatMessage(
        string jobId,
        string toolName,
        string label,
        bool isSuccess,
        string output,
        string? errorMessage = null)
        : this(jobId, toolName, label, isSuccess, output, errorMessage, DateTimeOffset.UtcNow)
    {
    }

    /// <inheritdoc />
    public override string ToString()
    {
        var status = IsSuccess ? "completed" : "failed";
        var body = string.IsNullOrWhiteSpace(ErrorMessage) ? Output : $"{ErrorMessage}\n{Output}".Trim();
        return $"<background-job id=\"{JobId}\" tool=\"{ToolName}\" label=\"{Label}\" status=\"{status}\">\n{body}\n</background-job>";
    }

    /// <inheritdoc />
    public override ChatBackgroundJobNotificationState Capture() => new(JobId, ToolName, Label, IsSuccess, Output, ErrorMessage);
}
