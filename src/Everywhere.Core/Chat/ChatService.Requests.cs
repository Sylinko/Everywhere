using System.Diagnostics;
using Everywhere.Harness;
using Everywhere.Statistics;
using Lucide.Avalonia;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Chat;

public sealed partial class ChatService
{
    private static bool IsCallerCancellation(Exception error, CancellationToken cancellationToken)
    {
        if (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) return false;
        var evidence = new ChatExceptionEvidence(error);
        ChatExceptionEvidenceExtractor.ExtractCommon(evidence, new ChatRequestFailureContext(cancellationToken));
        return evidence.IsCallerCancellation;
    }

    /// <summary>
    /// Assembles non-visible responses (topic generation, context compression, tool approval) with the same attempt statistics and reset rules.
    /// </summary>
    private async Task<AssembledResponse> ReadResponseAsync(
        ModelSession session,
        IReadOnlyList<MEAI.ChatMessage> history,
        MEAI.ChatOptions options,
        Guid chatContextId,
        Guid? messageNodeId,
        StatisticsModelInvocationPurpose purpose,
        ChatContext? context,
        ChatMessage? message,
        CancellationToken cancellationToken,
        ToolApprovalReviewer? approvalReviewer = null)
    {
        var assembler = new ResponseAssembler(session.Dialect);
        // Auxiliary requests retain their own invocation purpose and usage in statistics. They do
        // not accumulate into the visible answer's usage or token rate, even when it owns the node.
        await using var statistics = new RequestStatistics(
            this,
            session,
            chatContextId,
            messageNodeId,
            purpose,
            null);

        var retryActivity = default(IBusyActivity);
        try
        {
            await foreach (var update in session.StreamRequestAsync(history, options, cancellationToken: cancellationToken))
            {
                var hasToolOutput = update is ChatRequestUpdate.Content toolContent && session.HasToolCallUpdate(toolContent.Value);
                var hasGeneratedOutput = update is ChatRequestUpdate.Content output && (HasGeneratedOutput(output.Value) || hasToolOutput);
                await statistics.HandleAsync(update, hasGeneratedOutput, cancellationToken);
                if (hasGeneratedOutput)
                {
                    if (context is not null)
                    {
                        await context.DispatchAsync(() => retryActivity?.Dispose());
                    }

                    retryActivity = null;
                }

                switch (update)
                {
                    case ChatRequestUpdate.Content content:
                    {
                        assembler.AppendUpdate(content.Value);
                        // Only SDK-completed calls can end a review before stream exhaustion.
                        if (approvalReviewer is not null &&
                            await approvalReviewer.TrySubmitAsync(content.Value, assembler.FunctionCalls, cancellationToken))
                        {
                            await statistics.CompleteAsync();
                            return new AssembledResponse(string.Empty, [], new MEAI.ChatMessage(MEAI.ChatRole.Assistant, []));
                        }

                        break;
                    }
                    case ChatRequestUpdate.AttemptFailed { RetryDelay: not null } failed:
                    {
                        assembler.Reset();
                        if (context is not null && message is not null)
                        {
                            retryActivity = await context.DispatchAsync(() => UpdateRequestRetry(
                                context.Data,
                                message,
                                session.RequestMaxRetries,
                                retryActivity,
                                failed));
                        }

                        break;
                    }
                }
            }

            await statistics.CompleteAsync();
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            await statistics.CancelAsync();
            throw;
        }
        finally
        {
            if (context is not null)
            {
                await context.DispatchAsync(() => retryActivity?.Dispose());
            }
        }

        // Commit only the accepted attempt. Reasoning metadata must survive subsequent review
        // requests, including provider signatures and protected continuation data.
        var assistantContents = new List<MEAI.AIContent>();
        foreach (var segment in assembler.Blocks)
        {
            if (segment.IsReasoning)
            {
                if (ChatHistoryBuilder.CreateReasoningContent(segment.Text?.ToString(), segment.Metadata, session.Dialect) is { } reasoning)
                {
                    assistantContents.Add(reasoning);
                }
            }
            else if (segment.Text is { Length: > 0 } answerText)
            {
                assistantContents.Add(new MEAI.TextContent(answerText.ToString()));
            }
        }

        foreach (var call in assembler.FunctionCalls)
        {
            var properties = call.AdditionalProperties is { Count: > 0 } existing
                ? new MEAI.AdditionalPropertiesDictionary(existing)
                : new MEAI.AdditionalPropertiesDictionary();
            properties[ReasoningMetadata.DialectKey] = session.Dialect;
            assistantContents.Add(new MEAI.FunctionCallContent(call.CallId, call.Name, call.Arguments)
            {
                Exception = call.Exception,
                AdditionalProperties = properties
            });
        }

        var responseMessage = new MEAI.ChatMessage(MEAI.ChatRole.Assistant, assistantContents);
        return new AssembledResponse(assembler.GetAnswerText(), assembler.FunctionCalls, responseMessage);
    }

    private sealed class RequestStatistics(
        ChatService owner,
        ModelSession session,
        Guid chatContextId,
        Guid? messageNodeId,
        StatisticsModelInvocationPurpose purpose,
        Func<ChatUsageDetails, Task>? accumulateUsageAsync
    ) : IAsyncDisposable
    {
        public Guid AttemptInvocationId { get; private set; }

        public ChatUsageDetails AttemptUsage { get; private set; } = new();

        private readonly Stopwatch _attemptTimer = new();
        private TimeSpan? _firstOutputElapsed;
        private TimeSpan _lastOutputElapsed;
        private bool _isStarted;

        public async Task HandleAsync(ChatRequestUpdate update, bool hasGeneratedOutput, CancellationToken cancellationToken)
        {
            switch (update)
            {
                case ChatRequestUpdate.AttemptStarted started:
                {
                    AttemptUsage = new ChatUsageDetails();
                    _firstOutputElapsed = null;
                    _attemptTimer.Restart();
                    AttemptInvocationId = Guid.CreateVersion7();
                    started.Activity?.SetTag("everywhere.request.invocation_id", AttemptInvocationId);

                    await owner._statisticsRecorder.StartModelInvocationAsync(
                        new StatisticsModelInvocationDraft(
                            AttemptInvocationId,
                            owner._currentTurnEventId.Value,
                            chatContextId,
                            messageNodeId,
                            purpose,
                            session.Configuration.ModelId,
                            DateTimeOffset.UtcNow),
                        cancellationToken);
                    _isStarted = true;
                    break;
                }
                case ChatRequestUpdate.Content content:
                {
                    AttemptUsage.Update(content.Value);
                    if (hasGeneratedOutput)
                    {
                        _lastOutputElapsed = _attemptTimer.Elapsed;
                        if (_firstOutputElapsed is null)
                        {
                            _firstOutputElapsed = _lastOutputElapsed;
                            owner._timeToFirstTokenHistogram.Record(_lastOutputElapsed.TotalSeconds, GetModelTag(session.Configuration.ModelId));
                        }
                    }

                    break;
                }
                case ChatRequestUpdate.AttemptFailed failed:
                {
                    await FinishAsync(false, false, failed.Failure.Exception.GetType().FullName);
                    break;
                }
            }
        }

        private async Task FinishAsync(bool isSuccessful, bool isCanceled, string? errorType)
        {
            if (!_isStarted) return;
            _isStarted = false;

            var end = DateTimeOffset.UtcNow;
            _attemptTimer.Stop();
            if (_firstOutputElapsed is { } first)
            {
                AttemptUsage.RecordGenerationTiming(_attemptTimer.Elapsed - first, _lastOutputElapsed - first);
            }

            if (accumulateUsageAsync is not null)
            {
                await accumulateUsageAsync(AttemptUsage);
            }

            owner.RecordChatUsageMetrics(AttemptUsage, session.Configuration.ModelId);
            owner._chatRequestsCounter.Add(1, GetModelTag(session.Configuration.ModelId));

            await owner._statisticsRecorder.CompleteModelInvocationAsync(
                AttemptInvocationId,
                AttemptUsage,
                end,
                isSuccessful,
                isCanceled,
                errorType,
                CancellationToken.None);
        }

        public Task CompleteAsync() => FinishAsync(true, false, null);

        public Task CancelAsync() => FinishAsync(false, true, null);

        public async ValueTask DisposeAsync() => await FinishAsync(false, false, "ConsumerAbandoned");
    }

    private static bool HasGeneratedOutput(MEAI.ChatResponseUpdate update) =>
        update.Contents.AsValueEnumerable().Any(static item =>
            item is MEAI.TextContent { Text.Length: > 0 } or MEAI.TextReasoningContent { Text.Length: > 0 } or MEAI.DataContent { Data.IsEmpty: false });

    private static IBusyActivity UpdateRequestRetry(
        ChatData data,
        ChatMessage message,
        int maxRetries,
        IBusyActivity? activity,
        ChatRequestUpdate.AttemptFailed failed)
    {
        var header = maxRetries == -1 ?
            new FormattedDynamicLocaleKey(LocaleKey.ChatRequest_RetryUnlimited, new DirectLocaleKey(failed.Failure.AttemptNumber.ToString())) :
            new FormattedDynamicLocaleKey(
                LocaleKey.ChatRequest_RetryCount,
                new DirectLocaleKey(failed.Failure.AttemptNumber.ToString()),
                new DirectLocaleKey(maxRetries.ToString()));

        if (activity is null)
        {
            return data.StartActivity(
                LucideIconKind.WifiSync,
                header,
                true,
                failed.Failure.Exception.FriendlyMessageKey,
                message);
        }

        activity.HeaderKey = header;
        activity.SecondaryHeaderKey = failed.Failure.Exception.FriendlyMessageKey;
        return activity;
    }

    private readonly record struct AssembledResponse(
        string Text,
        IReadOnlyList<MEAI.FunctionCallContent> FunctionCalls,
        MEAI.ChatMessage Message);
}