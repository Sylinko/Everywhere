using System.Text;
using Everywhere.Chat.Plugins;
using Everywhere.Harness;
using Everywhere.Statistics;
using Lucide.Avalonia;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Chat;

partial class ChatService
{
    private async Task InvokeFunctionsAsync(
        GenerationContext generationContext,
        ChatContext chatContext,
        AssistantChatMessage assistant,
        ModelInvocationResult invocation,
        CancellationToken cancellationToken)
    {
        var session = generationContext.Session;
        var scope = generationContext.PluginScope;
        var (span, batch) = CreateToolBatch(session, scope, invocation.FunctionCalls);

        // Publish the complete, ordered batch before any side effects. Results and
        // transient previews update their own slots; completion never reorders them.
        await chatContext.DispatchAsync(() => assistant.AddSpan(span));

        var activeBackgroundLock = new Lock();
        var activeBackgroundableContexts = new List<FunctionCallContext>();

        void HandleSteerRequested()
        {
            FunctionCallContext[] snapshot;
            using (activeBackgroundLock.EnterScope())
            {
                if (activeBackgroundableContexts.Count == 0) return;
                snapshot = [.. activeBackgroundableContexts];
            }

            foreach (var activeContext in snapshot)
            {
                activeContext.TryMoveToBackground();
            }
        }

        chatContext.PendingInputs.SteerRequested += HandleSteerRequested;
        try
        {
            for (var start = 0; start < batch.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chatContext.PendingInputs.HasPendingSteers)
                {
                    await MarkRemainingToolsSkippedAsync(chatContext, batch, start);
                    break;
                }

                var end = start + 1;
                if (batch[start].Binding?.Function.SupportsParallelInvoke is true)
                {
                    while (end < batch.Count && batch[end].Binding?.Function.SupportsParallelInvoke is true)
                    {
                        end++;
                    }
                }

                if (end == start + 1)
                {
                    await RunAsync(batch[start]);
                }
                else
                {
                    var running = new Task[end - start];
                    for (var i = start; i < end; i++)
                    {
                        running[i - start] = RunAsync(batch[i]);
                    }

                    await Task.WhenAll(running);
                }

                start = end;
            }
        }
        finally
        {
            chatContext.PendingInputs.SteerRequested -= HandleSteerRequested;
            await chatContext.DispatchAsync(() =>
            {
                foreach (var item in batch)
                {
                    Finish(item.Message);
                }

                span.FinishedAt = DateTimeOffset.UtcNow;
            });
        }

        async Task RunAsync(ToolInvocation item)
        {
            var (call, message, binding, suggestions) = item;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (chatContext.PendingInputs.HasPendingSteers)
                {
                    await chatContext.DispatchAsync(() =>
                    {
                        message.AddResult(CreateSteerSkippedResult(call, binding));
                        Finish(message);
                    });
                    return;
                }

                if (binding is null)
                {
                    var reason = new StringBuilder(scope is null ? "Tool calling is disabled by the user" : $"Tool '{call.Name}' is not available.");
                    if (suggestions is { Count: > 0 })
                    {
                        reason.Append(" Did you mean:");
                        foreach (var name in suggestions)
                        {
                            reason.Append(' ').AppendLine(name);
                        }
                    }

                    await chatContext.DispatchAsync(() =>
                    {
                        message.Icon = LucideIconKind.X;
                        var errorKey = scope is null ?
                            LocaleKey.HandledFunctionInvokingException_FunctionCallingDisabled :
                            LocaleKey.HandledFunctionInvokingException_FunctionNotFound;
                        message.ErrorMessageKey = new FormattedDynamicLocaleKey(
                            errorKey,
                            new DirectLocaleKey(call.Name));
                        message.AddResult(new ToolResultRecord(call.Name, null, call.CallId, reason.ToString()));
                    });
                    var status = scope is null ? StatisticsToolInvocationStatus.Disabled : StatisticsToolInvocationStatus.NotFound;
                    await RecordToolInvocationAsync(chatContext.Data, call, null, status, cancellationToken);
                    return;
                }

                var supportsBackgrounding = binding.Function.SupportsBackgrounding;
                var jobCts = chatContext.Root.CreateBackgroundCancellationTokenSource();
                var transferredToBackground = false;
                FunctionCallContext? context = null;
                CancellationTokenRegistration foregroundRegistration = default;

                try
                {
                    // The invocation captures approval mode on entry, not when the batch was registered.
                    context = await chatContext.DispatchAsync(() => new FunctionCallContext(
                        generationContext.Services,
                        chatContext,
                        binding,
                        message,
                        call,
                        _settings.Plugin.ToolBypassApprovalRulesets,
                        chatContext.GenerationContext ?? generationContext,
                        ReviewToolApprovalAsync,
                        supportsBackgrounding,
                        jobCts.Token));

                    if (supportsBackgrounding)
                    {
                        using (activeBackgroundLock.EnterScope())
                        {
                            activeBackgroundableContexts.Add(context);
                        }
                    }

                    foregroundRegistration = cancellationToken.Register(() =>
                    {
                        if (context.TryCancelForeground())
                        {
                            // ReSharper disable once AccessToDisposedClosure
                            jobCts.Cancel();
                        }
                    });

                    using var ambient = generationContext.EnterFunctionCallContext(context);
                    var friendly = binding.Function.BeforeCallHook?.Invoke(call);
                    var executionTask = InvokeFunctionAsync(session, call, context, friendly, jobCts.Token);
                    if (supportsBackgrounding)
                    {
                        var completedTask = await Task.WhenAny(executionTask, context.BackgroundMovedTask);
                        if (!executionTask.IsCompleted && (completedTask != executionTask || context.IsBackgrounded))
                        {
                            transferredToBackground = true;
                            await foregroundRegistration.DisposeAsync();

                            var activeContext = context;
                            var hasGenerationHold = generationContext.TryRetainBackgroundHold();
                            async Task<(ToolResultRecord Record, Exception? Error)> TrackDetachedExecutionAsync()
                            {
                                try
                                {
                                    return await executionTask.ConfigureAwait(false);
                                }
                                finally
                                {
                                    await activeContext.DisposeAsync().ConfigureAwait(false);
                                    if (hasGenerationHold)
                                    {
                                        await generationContext.ReleaseBackgroundHoldAsync().ConfigureAwait(false);
                                    }
                                }
                            }

                            var ownerTurn = message.Owner?.Turn ?? generationContext.AppendTailNode.Turn;
                            var job = chatContext.Root.BackgroundJobs.RegisterDetachedJob(
                                chatContext,
                                ownerTurn,
                                call.Name,
                                BuildBackgroundJobLabel(call, binding),
                                message,
                                TrackDetachedExecutionAsync(),
                                jobCts,
                                HandleBackgroundJobCompleted);

                            await activeContext.DetachPresentationAsync();
                            await chatContext.DispatchAsync(() =>
                            {
                                message.AddResult(
                                    new ToolResultRecord(
                                        call.Name,
                                        binding.Plugin.Key,
                                        call.CallId,
                                        $"The tool call '{call.Name}' moved to background execution (job_id: {job.JobId}). You will receive a <background-job> notification when it completes, or you can use manage_background_jobs to inspect or stop it."));
                                Finish(message, isForegroundCanceled: false);
                            });
                            return;
                        }

                        context.TryCompleteForeground();
                    }

                    var result = await executionTask;
                    await chatContext.DispatchAsync(() =>
                    {
                        message.AddResult(result.Record);
                        if (result.Error is { } error)
                        {
                            message.ErrorMessageKey ??= error.GetFriendlyMessage();
                        }
                    });
                    cancellationToken.ThrowIfCancellationRequested();
                }
                finally
                {
                    if (supportsBackgrounding && context is not null)
                    {
                        using (activeBackgroundLock.EnterScope())
                        {
                            activeBackgroundableContexts.Remove(context);
                        }
                    }

                    if (!transferredToBackground)
                    {
                        await foregroundRegistration.DisposeAsync();
                        jobCts.Dispose();
                        if (context is not null)
                        {
                            await context.DisposeAsync();
                        }
                    }
                }
            }
            finally
            {
                await chatContext.DispatchAsync(() => Finish(message));
            }
        }

        void Finish(FunctionCallChatMessage message, bool isForegroundCanceled = true)
        {
            if (!message.IsBusy) return;
            message.FinishedAt = DateTimeOffset.UtcNow;
            message.IsBusy = false;
            if (isForegroundCanceled && cancellationToken.IsCancellationRequested)
            {
                message.ErrorMessageKey ??= new DynamicLocaleKey(LocaleKey.FriendlyExceptionMessage_OperationCanceled);
            }
        }
    }

    private static async Task AppendSkippedFunctionCallsAsync(
        GenerationContext generationContext,
        ChatContext chatContext,
        AssistantChatMessage assistant,
        ModelInvocationResult invocation)
    {
        var (span, batch) = CreateToolBatch(generationContext.Session, generationContext.PluginScope, invocation.FunctionCalls);
        await chatContext.DispatchAsync(() =>
        {
            assistant.AddSpan(span);
            foreach (var item in batch)
            {
                item.Message.AddResult(CreateSteerSkippedResult(item.Call, item.Binding));
                item.Message.FinishedAt = DateTimeOffset.UtcNow;
                item.Message.IsBusy = false;
            }

            span.FinishedAt = DateTimeOffset.UtcNow;
        });
    }

    private static Task MarkRemainingToolsSkippedAsync(
        ChatContext chatContext,
        List<ToolInvocation> batch,
        int startIndex)
    {
        return chatContext.DispatchAsync(() =>
        {
            for (var i = startIndex; i < batch.Count; i++)
            {
                var item = batch[i];
                if (item.Message is { IsBusy: false, Results.Length: > 0 }) continue;
                item.Message.AddResult(CreateSteerSkippedResult(item.Call, item.Binding));
                item.Message.FinishedAt = DateTimeOffset.UtcNow;
                item.Message.IsBusy = false;
            }
        });
    }

    private static (AssistantChatMessageFunctionCallSpan Span, List<ToolInvocation> Batch) CreateToolBatch(
        ModelSession session,
        IChatPluginScope? scope,
        IReadOnlyList<MEAI.FunctionCallContent> functionCalls)
    {
        var span = new AssistantChatMessageFunctionCallSpan
        {
            Metadata = MetadataDictionary.FromDictionary(
                new Dictionary<string, object?>
                {
                    [ReasoningMetadata.DialectKey] = session.Dialect
                })
        };

        var batch = new List<ToolInvocation>(functionCalls.Count);
        foreach (var call in functionCalls)
        {
            if (call.CallId.IsNullOrEmpty())
            {
                throw new InvalidOperationException("Tool call must have an ID.");
            }

            var binding = default(ToolBinding);
            var suggestions = default(IReadOnlyList<string>);
            scope?.TryGetBinding(call.Name, out binding, out suggestions);
            var message = new FunctionCallChatMessage(
                binding?.Function.Icon ?? binding?.Plugin.Icon ?? LucideIconKind.Hammer,
                binding?.Function.HeaderKey ?? new DirectLocaleKey(call.Name))
            {
                IsBusy = true
            };
            var callMetadata = new Dictionary<string, object?>
            {
                [ReasoningMetadata.DialectKey] = session.Dialect
            };
            if (call.AdditionalProperties?.TryGetValue(ReasoningMetadata.ThoughtSignatureKey, out var rawSignature) is true &&
                rawSignature is string { Length: > 0 } signature)
            {
                callMetadata[ReasoningMetadata.ThoughtSignatureKey] = signature;
            }

            message.AddCall(
                new ToolCallRecord(
                    call.Name,
                    binding?.Plugin.Key,
                    call.CallId,
                    call.Arguments is null ? null : new Dictionary<string, object?>(call.Arguments))
                {
                    Metadata = callMetadata
                });
            span.Add(message);
            batch.Add(new ToolInvocation(call, message, binding, suggestions));
        }

        return (span, batch);
    }

    private static ToolResultRecord CreateSteerSkippedResult(MEAI.FunctionCallContent call, ToolBinding? binding) =>
        new(
            call.Name,
            binding?.Plugin.Key,
            call.CallId,
            "Skipped because the user sent a steering message.");

    private static string BuildBackgroundJobLabel(MEAI.FunctionCallContent call, ToolBinding binding)
    {
        if (call.Arguments is not null)
        {
            foreach (var key in new[] { "title", "command", "path", "query" })
            {
                if (!call.Arguments.TryGetValue(key, out var rawValue)) continue;
                var value = rawValue switch
                {
                    string text => text,
                    System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => element.GetString(),
                    _ => rawValue?.ToString()
                };
                if (string.IsNullOrWhiteSpace(value)) continue;
                var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
                if (normalized.Length == 0) continue;
                return normalized.Length <= 80 ? normalized : string.Concat(normalized.AsSpan(0, 77), "...");
            }
        }

        var header = binding.Function.HeaderKey.ToString();
        return string.IsNullOrWhiteSpace(header) ? call.Name : header;
    }

    private sealed record ToolInvocation(
        MEAI.FunctionCallContent Call,
        FunctionCallChatMessage Message,
        ToolBinding? Binding,
        IReadOnlyList<string>? Suggestions
    );
}
