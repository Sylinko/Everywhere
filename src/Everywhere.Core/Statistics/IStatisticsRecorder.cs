using Everywhere.Chat;

namespace Everywhere.Statistics;

/// <summary>
/// Write-side API for local usage statistics.
/// </summary>
/// <remarks>
/// Implementations must not let statistics failures interrupt chat or tool execution.
/// </remarks>
public interface IStatisticsRecorder
{
    /// <summary>
    /// Records the topic row for a chat context if it has not been recorded on this device.
    /// </summary>
    Task<Guid?> RecordTopicAsync(ChatContext chatContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a user-request turn and ensures the owning topic is present.
    /// </summary>
    /// <returns>The created turn event id, or <c>null</c> when statistics recording is disabled or failed.</returns>
    Task<Guid?> RecordTurnAsync(
        ChatContext chatContext,
        ChatMessageNode? userNode,
        StatisticsTurnKind kind,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records numeric visual context measurements without storing captured content.
    /// </summary>
    Task RecordVisualContextAsync(StatisticsVisualContextDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a model invocation event before the provider request begins.
    /// </summary>
    Task StartModelInvocationAsync(StatisticsModelInvocationDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a model invocation event with final status and provider-reported usage.
    /// </summary>
    Task CompleteModelInvocationAsync(
        Guid invocationId,
        ChatUsageDetails usageDetails,
        DateTimeOffset finishedAt,
        bool isSuccess,
        bool isCanceled,
        string? errorType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a tool invocation event before permission checks or function execution.
    /// </summary>
    Task StartToolInvocationAsync(StatisticsToolInvocationDraft draft, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a tool invocation event with its final outcome.
    /// </summary>
    Task CompleteToolInvocationAsync(
        Guid invocationId,
        StatisticsToolInvocationStatus status,
        DateTimeOffset finishedAt,
        CancellationToken cancellationToken = default);
}
