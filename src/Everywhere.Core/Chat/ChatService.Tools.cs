using System.Text;
using Everywhere.Harness;
using Everywhere.Statistics;
using Lucide.Avalonia;
using Meai = Microsoft.Extensions.AI;

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
        var span = new AssistantChatMessageFunctionCallSpan
        {
            Metadata = MetadataDictionary.FromDictionary(
                new Dictionary<string, object?>
                {
                    [ReasoningMetadata.DialectKey] = session.Dialect
                })
        };

        var batch = new List<ToolInvocation>(invocation.FunctionCalls.Count);
        foreach (var call in invocation.FunctionCalls)
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

        // Publish the complete, ordered batch before any side effects. Results and
        // transient previews update their own slots; completion never reorders them.
        await chatContext.DispatchAsync(() => assistant.AddSpan(span));

        try
        {
            for (var start = 0; start < batch.Count;)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                // The invocation captures approval mode on entry, not when the batch was registered.
                await using var context = await chatContext.DispatchAsync(() => new FunctionCallContext(
                    generationContext.Services,
                    chatContext,
                    binding,
                    message,
                    call,
                    _settings.Plugin.ToolBypassApprovalRulesets,
                    chatContext.GenerationContext ?? throw new InvalidOperationException("No generation is active."),
                    ReviewToolApprovalAsync));
                using var ambient = context.GenerationContext.EnterFunctionCallContext(context);
                var friendly = binding.Function.GetFriendlyCallContent(call);
                if (friendly is not null)
                {
                    await context.AppendBlockAsync(friendly);
                }

                var result = await InvokeFunctionAsync(session, call, context, friendly, cancellationToken);
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
                await chatContext.DispatchAsync(() => Finish(message));
            }
        }

        void Finish(FunctionCallChatMessage message)
        {
            if (!message.IsBusy) return;
            message.FinishedAt = DateTimeOffset.UtcNow;
            message.IsBusy = false;
            if (cancellationToken.IsCancellationRequested)
            {
                message.ErrorMessageKey ??= new DynamicLocaleKey(LocaleKey.FriendlyExceptionMessage_OperationCanceled);
            }
        }
    }

    private sealed record ToolInvocation(
        Meai.FunctionCallContent Call,
        FunctionCallChatMessage Message,
        ToolBinding? Binding,
        IReadOnlyList<string>? Suggestions
    );
}