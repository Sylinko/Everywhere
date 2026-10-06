using System.Text;
using Avalonia.Threading;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Statistics;
using Lucide.Avalonia;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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
    /// Assembles non-visible responses with the same attempt statistics and reset rules.
    /// </summary>
    private async Task<AssembledResponse> ReadResponseAsync(
        KernelMixin mixin,
        ChatHistory history,
        PromptExecutionSettings settings,
        Kernel? kernel,
        Guid chatContextId,
        Guid? messageNodeId,
        StatisticsModelInvocationPurpose purpose,
        ChatContext? context,
        ChatMessage? message,
        CancellationToken cancellationToken,
        ToolApprovalReviewer? approvalReviewer = null)
    {
        var text = new StringBuilder();
        var metadata = new Dictionary<string, object?>();
        var segments = new List<(bool IsReasoning, StringBuilder Text, Dictionary<string, object?> Metadata)>();
        var calls = new FunctionCallContentBuilder();
        await using var statistics = new RequestStatistics(
            this,
            mixin,
            chatContextId,
            messageNodeId,
            purpose,
            (message as AssistantChatMessage)?.UsageDetails);

        var retryActivity = default(IBusyActivity);
        try
        {
            await foreach (var update in mixin.StreamRequestAsync(history, settings, kernel, cancellationToken: cancellationToken))
            {
                var hasToolOutput = update is ChatRequestUpdate.Content toolContent && calls.Append(toolContent.Value);
                var hasGeneratedOutput = update is ChatRequestUpdate.Content output && (HasGeneratedOutput(output.Value) || hasToolOutput);
                await statistics.HandleAsync(update, hasGeneratedOutput, cancellationToken);
                if (hasGeneratedOutput)
                {
                    retryActivity?.Dispose();
                    retryActivity = null;
                }
                switch (update)
                {
                    case ChatRequestUpdate.Content content:
                    {
                        if (content.Value.Metadata is not null)
                        {
                            foreach (var (key, value) in content.Value.Metadata) metadata[key] = value;
                        }
                        foreach (var item in content.Value.Items)
                        {
                            switch (item)
                            {
                                case StreamingReasoningContent reasoning:
                                    AppendSegment(reasoning.Text, true, reasoning.Metadata);
                                    break;
                                case StreamingTextContent textContent:
                                    text.Append(textContent.Text);
                                    AppendSegment(textContent.Text, false, textContent.Metadata);
                                    break;
                                case StreamingChatMessageContent chatContent:
                                    text.Append(chatContent.Content);
                                    AppendSegment(chatContent.Content, false, chatContent.Metadata);
                                    break;
                            }
                        }
                        // Only SDK-completed calls can end a review before stream exhaustion.
                        // Argument fragments that merely parse as JSON are not completion evidence.
                        if (approvalReviewer is not null && kernel is not null &&
                            await approvalReviewer.TrySubmitAsync(content.Value, calls, kernel, cancellationToken))
                        {
                            await statistics.CompleteAsync();
                            return new AssembledResponse(string.Empty, [], new ChatMessageContent(AuthorRole.Assistant, (string?)null));
                        }
                        break;
                    }
                    case ChatRequestUpdate.AttemptFailed { RetryDelay: not null } failed:
                    {
                        text.Clear();
                        metadata.Clear();
                        segments.Clear();
                        calls = new FunctionCallContentBuilder();
                        if (context is not null && message is not null)
                        {
                            retryActivity = await UpdateRequestRetryAsync(context, message, mixin.RequestMaxRetries, retryActivity, failed);
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
            retryActivity?.Dispose();
        }

        // Commit only the accepted attempt. Reasoning metadata must survive subsequent review
        // requests, including provider signatures and protected continuation data.
        var response = new ChatMessageContent(AuthorRole.Assistant, (string?)null) { Metadata = metadata };
        foreach (var segment in segments)
        {
            response.Items.Add(segment.IsReasoning ?
                new ReasoningContent(segment.Text.ToString()) { Metadata = segment.Metadata } :
                new TextContent(segment.Text.ToString()) { Metadata = segment.Metadata });
        }

        var functionCalls = calls.Build();
        if (functionCalls.Count != calls.Count)
        {
            throw new HandledChatException.InvalidResponse(new InvalidOperationException("The response stream contained incomplete tool calls."));
        }

        foreach (var call in functionCalls) response.Items.Add(call);
        return new AssembledResponse(text.ToString(), functionCalls, response);

        void AppendSegment(string? value, bool isReasoning, IReadOnlyDictionary<string, object?>? itemMetadata)
        {
            if (segments.Count == 0 || segments[^1].IsReasoning != isReasoning)
            {
                segments.Add((isReasoning, new StringBuilder(), new Dictionary<string, object?>()));
            }

            var segment = segments[^1];
            segment.Text.Append(value);

            if (itemMetadata is not null)
            {
                foreach (var (key, itemValue) in itemMetadata)
                {
                    segment.Metadata[key] = itemValue;
                }
            }
        }
    }

    // Request consumers own statistics, independently of the transport retry catch boundary.
    // A single scope is shared by visible streaming and assembled-response consumers.
    private sealed class RequestStatistics(
        ChatService owner,
        KernelMixin mixin,
        Guid chatContextId,
        Guid? messageNodeId,
        StatisticsModelInvocationPurpose purpose,
        ChatUsageDetails? accumulatedUsage
    ) : IAsyncDisposable
    {
        public Guid AttemptInvocationId { get; private set; }

        public ChatUsageDetails AttemptUsage { get; private set; } = new();

        private DateTimeOffset _startedAt;
        private DateTimeOffset? _firstTokenAt;
        private bool _isStarted;

        public async Task HandleAsync(ChatRequestUpdate update, bool hasGeneratedOutput, CancellationToken cancellationToken)
        {
            switch (update)
            {
                case ChatRequestUpdate.AttemptStarted started:
                {
                    AttemptUsage = new ChatUsageDetails();
                    _firstTokenAt = null;
                    _startedAt = DateTimeOffset.UtcNow;
                    AttemptInvocationId = Guid.CreateVersion7();
                    started.Activity?.SetTag("everywhere.request.invocation_id", AttemptInvocationId);

                    await owner._statisticsRecorder.StartModelInvocationAsync(
                        new StatisticsModelInvocationDraft(
                            AttemptInvocationId,
                            owner._currentTurnEventId.Value,
                            chatContextId,
                            messageNodeId,
                            purpose,
                            mixin.Configuration.ModelId,
                            _startedAt),
                        cancellationToken);
                    _isStarted = true;
                    break;
                }
                case ChatRequestUpdate.Content content:
                {
                    AttemptUsage.Update(content.Value);
                    if (_firstTokenAt is null && hasGeneratedOutput)
                    {
                        _firstTokenAt = DateTimeOffset.UtcNow;
                        owner._timeToFirstTokenHistogram.Record(
                            (_firstTokenAt.Value - _startedAt).TotalSeconds,
                            GetModelTag(mixin.Configuration.ModelId));
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
            var seconds = _firstTokenAt is { } first ? Math.Max(0, (end - first).TotalSeconds) : 0;
            var invocationUsage = new ChatUsageDetails();
            invocationUsage.Accumulate(AttemptUsage, seconds);
            accumulatedUsage?.Accumulate(AttemptUsage, seconds);
            owner.RecordChatUsageMetrics(AttemptUsage, mixin.Configuration.ModelId);
            owner._chatRequestsCounter.Add(1, GetModelTag(mixin.Configuration.ModelId));

            await owner._statisticsRecorder.CompleteModelInvocationAsync(
                AttemptInvocationId,
                invocationUsage,
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

    private static bool HasGeneratedOutput(StreamingChatMessageContent content) =>
        content.Items.AsValueEnumerable().Any(item => item is
            StreamingTextContent { Text.Length: > 0 } or StreamingChatMessageContent { Content.Length: > 0 } or
            StreamingReasoningContent { Text.Length: > 0 } || item.InnerContent is BinaryContent { Data: not null });

    private static async Task<IBusyActivity> UpdateRequestRetryAsync(
        ChatContext context,
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
            return await context.Presentation.SetBusyActivityAsync(
                LucideIconKind.WifiSync,
                header,
                true,
                failed.Failure.Exception.FriendlyMessageKey,
                message);
        }

        await Dispatcher.UIThread.InvokeOnDemandAsync(() =>
        {
            activity.HeaderKey = header;
            activity.SecondaryHeaderKey = failed.Failure.Exception.FriendlyMessageKey;
        });
        return activity;
    }

    private readonly record struct AssembledResponse(string Text, IReadOnlyList<FunctionCallContent> FunctionCalls, ChatMessageContent Message);
}