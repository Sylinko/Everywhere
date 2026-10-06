using System.Diagnostics;
using Everywhere.Common;
using Everywhere.Chat;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.AI;

public abstract partial class KernelMixin
{
    private static readonly ActivitySource RequestActivitySource = new(typeof(KernelMixin).FullName.NotNull(), App.Version);

    /// <summary>
    /// Streams one logical request with fixed input and bounded diagnostics. Only SDK stream
    /// acquisition, reads and completion evidence are inside the retry boundary. Consumer work
    /// and tools remain outside it. Disposing the enumeration never starts another attempt.
    /// </summary>
    public async IAsyncEnumerable<ChatRequestUpdate> StreamRequestAsync(
        ChatHistory history,
        PromptExecutionSettings executionSettings,
        Kernel? kernel = null,
        ChatRequestOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var maxRetries = Math.Max(options?.MaxRetries ?? RequestMaxRetries, -1);
        var input = new ChatHistory(history);
        var settings = executionSettings.Clone();
        var failures = new Queue<ChatRequestFailure>(10);
        var attemptNumber = 0;
        var totalFailures = 0L;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptNumber++;

            var attemptActivity = RequestActivitySource.StartChatActivity("invoke_agent", Configuration);
            attemptActivity?.SetTag("gen_ai.request.attempt", attemptNumber);

            TimeSpan? delay;
            Exception? error = null;
            var hasClosedAttempt = false;
            var attemptUsage = new ChatUsageDetails();
            try
            {
                yield return new ChatRequestUpdate.AttemptStarted(attemptNumber, attemptActivity);
                // Restore the attempt after each yield, as in MEAI's OpenTelemetryChatClient.
                // Workaround for https://github.com/dotnet/runtime/issues/47802.
                if (attemptActivity is not null) Activity.Current = attemptActivity;

                var enumerator = default(IAsyncEnumerator<StreamingChatMessageContent>);
                var completionFailure = default(HandledChatException);
                try
                {
                    try
                    {
                        enumerator = ChatCompletionService.GetStreamingChatMessageContentsAsync(
                            [.. input],
                            settings.Clone(),
                            kernel,
                            cancellationToken).GetAsyncEnumerator(cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        error = ex;
                    }

                    while (enumerator is not null && error is null)
                    {
                        var hasNext = false;
                        try
                        {
                            hasNext = await enumerator.MoveNextAsync();
                        }
                        catch (Exception ex)
                        {
                            error = ex;
                        }

                        if (!hasNext) break;
                        var content = enumerator.Current;
                        attemptUsage.Update(content);
                        completionFailure ??= ChatExceptionNormalizer.FromCompletion(content, this);

                        yield return new ChatRequestUpdate.Content(content);
                        if (attemptActivity is not null) Activity.Current = attemptActivity;
                    }
                }
                finally
                {
                    // Preserve the primary request diagnosis when its cleanup also fails.
                    if (enumerator is not null)
                    {
                        // Early disposal skips the code following yield return.
                        if (attemptActivity is not null) Activity.Current = attemptActivity;

                        try
                        {
                            await enumerator.DisposeAsync();
                        }
                        catch when (error is not null) { }
                    }
                }

                error ??= completionFailure;
                if (error is null)
                {
                    hasClosedAttempt = true;
                    yield break;
                }

                var normalized = ChatExceptionNormalizer.Handle(error, this, new ChatRequestFailureContext(cancellationToken));
                if (normalized is HandledChatException.Canceled.ByCaller)
                {
                    throw new OperationCanceledException("The request was canceled by its caller.", error, cancellationToken);
                }

                var handled = normalized as HandledChatException ?? new HandledChatException.Unknown(normalized);
                totalFailures++;

                var failure = new ChatRequestFailure(attemptNumber, DateTimeOffset.UtcNow, handled);
                if (failures.Count == 10) failures.Dequeue(); // Keep only the last 10 failures.
                failures.Enqueue(failure);

                var recovery = handled.Recovery;
                delay = recovery is ChatExceptionRecovery.Retry retry && (maxRetries == -1 || attemptNumber <= maxRetries) ?
                    GetRetryDelay(attemptNumber, retry.RetryAfter) :
                    null;
                attemptActivity?.SetStatus(ActivityStatusCode.Error, handled.GetType().FullName);
                attemptActivity?.SetTag("error.type", handled.GetType().FullName);
                attemptActivity?.SetTag("gen_ai.request.recovery", recovery.GetType().Name);
                hasClosedAttempt = true;

                yield return new ChatRequestUpdate.AttemptFailed(failure, delay);
                if (attemptActivity is not null) Activity.Current = attemptActivity;

                if (delay is null) throw new ChatRequestException(handled, [.. failures], totalFailures);
            }
            finally
            {
                // Also cover disposal after AttemptStarted or a terminal update.
                if (attemptActivity is not null) Activity.Current = attemptActivity;

                if (!hasClosedAttempt)
                {
                    attemptActivity?.SetStatus(
                        ActivityStatusCode.Error,
                        cancellationToken.IsCancellationRequested ? "Canceled" : "ConsumerAbandoned");
                }
                attemptActivity.SetChatUsageTags(attemptUsage);
                attemptActivity?.Dispose();
            }

            await Task.Delay(delay.GetValueOrDefault(), cancellationToken);
        }
    }

    /// <summary>
    /// Calculates the retry delay based on the retry number and any server-specified delay.
    /// Formula: min(30, 2^(min(retryNumber - 1, 5)) * (0.8 + random(0, 0.4))).
    /// Honors a longer server delay up to 30 seconds. A larger server delay stops retrying.
    /// </summary>
    /// <param name="retryNumber"></param>
    /// <param name="serverDelay"></param>
    /// <returns></returns>
    private static TimeSpan? GetRetryDelay(int retryNumber, TimeSpan? serverDelay)
    {
        if (serverDelay > TimeSpan.FromSeconds(30)) return null;
        var local = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(retryNumber - 1, 5)) * (0.8 + Random.Shared.NextDouble() * 0.4)));
        return serverDelay is { } delay && delay > local ? delay : local;
    }
}