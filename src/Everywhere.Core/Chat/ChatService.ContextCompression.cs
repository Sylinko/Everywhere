using Everywhere.AI;
using Everywhere.AI.Prompts;
using Everywhere.Common;
using Everywhere.Statistics;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

partial class ChatService
{
    private async Task<CompressionResult> CompactContextAsync(
        ChatContext chatContext,
        GenerationContext context,
        ContextCompressionTrigger trigger,
        CompressionBoundary boundary,
        CancellationToken cancellationToken)
    {
        // Select from one branch snapshot before adding the attempt's timeline row.
        var nodes = chatContext.Items;
        var boundaryIndex = ResolveCompressionBoundary(nodes, boundary);
        var coveredThroughNodeId = boundaryIndex >= 0 ? nodes[boundaryIndex].Id : Guid.Empty;
        var configuration = context.KernelMixin.Configuration;
        var usageBefore = chatContext.ContextUsage.Snapshot;
        var compressionMessage = new ContextCompressionChatMessage(
            coveredThroughNodeId,
            configuration.ModelId,
            trigger,
            usageBefore.TotalTokenCount,
            configuration.ContextLimit > 0 ? configuration.ContextLimit : null);

        await using var compactionScope = await chatContext.BeginContextCompactionAsync();
        var compressionNode = chatContext.Add(compressionMessage);

        try
        {
            if (boundaryIndex < 0)
            {
                throw new ContextCompressionOutputException(
                    "There is no conversation history to compact.",
                    LocaleKey.ContextCompression_Error_NoHistory);
            }

            // A preceding compaction can append its row after the current assistant placeholder.
            // Keep those checkpoints available without pulling the current user turn into the
            // source. The history builder ignores checkpoints whose anchor is outside this prefix.
            var sourceNodes = nodes
                .AsValueEnumerable()
                .Take(boundaryIndex + 1)
                .Concat(
                    nodes
                        .AsValueEnumerable()
                        .Skip(boundaryIndex + 1)
                        .Where(static node => node.Message is ContextCompressionChatMessage { HasSummary: true }))
                .ToArray();

            var messages = ChatHistoryBuilder.SelectContextMessages(
                sourceNodes,
                context.MaxContextRounds,
                configuration.ContextLimit,
                out var wasSourceHistoryTrimmed).ToList();

            string summary;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    summary = await RequestCompressionSummaryAsync(
                        chatContext,
                        context,
                        messages,
                        compressionNode,
                        cancellationToken);
                    break;
                }
                catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
                {
                    throw;
                }
                catch (ContextCompressionOutputException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var handledException = ChatExceptionNormalizer.Handle(ex, context.KernelMixin, new ChatRequestFailureContext(cancellationToken));
                    if (ChatExceptionNormalizer.GetFinalFailure(handledException) is not HandledChatException.InvalidRequest.ContextLengthExceeded ||
                        !TryTrimOldestConversationUnit(messages))
                    {
                        throw handledException;
                    }

                    wasSourceHistoryTrimmed = true;
                }
            }

            compressionMessage.Complete(summary, wasSourceHistoryTrimmed, DateTimeOffset.UtcNow);
            await chatContext.MarkContextCompactedAsync(configuration.ModelId, configuration.ContextLimit);
            return new CompressionResult(true, null);
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            compressionMessage.Cancel(DateTimeOffset.UtcNow);
            throw;
        }
        catch (ContextCompressionOutputException ex)
        {
            compressionMessage.Fail(new DynamicLocaleKey(ex.LocaleKey), DateTimeOffset.UtcNow);
            _logger.LogWarning(ex, "Context compression returned an invalid response");
            return new CompressionResult(false, ex);
        }
        catch (Exception ex)
        {
            ex = ChatExceptionNormalizer.Handle(ex, context.KernelMixin, new ChatRequestFailureContext(cancellationToken));
            compressionMessage.Fail(ex.GetFriendlyMessage(), DateTimeOffset.UtcNow);
            _logger.LogError(ex, "Failed to compact chat context");
            return new CompressionResult(false, ex);
        }

        static bool TryTrimOldestConversationUnit(List<ChatMessage> messages)
        {
            if (messages.Count <= 1) return false;

            var compressionIndex = messages.FindLastIndex(static message => message is ContextCompressionChatMessage { HasSummary: true });
            if (compressionIndex > 0)
            {
                // Retained user messages are intentionally placed before the prior summary. They are
                // useful verbatim context, but are the first optional history to drop when the
                // compression request itself exceeds the provider limit.
                messages.RemoveAt(0);
                return true;
            }

            var startIndex = compressionIndex + 1;
            var firstUserIndex = messages.FindIndex(
                startIndex,
                static message => message.Role.Label == AuthorRole.User.Label && message is not ContextCompressionChatMessage);
            if (firstUserIndex < 0)
            {
                if (messages.Count - startIndex > 1)
                {
                    messages.RemoveAt(startIndex);
                    return true;
                }

                if (compressionIndex < 0) return false;

                messages.RemoveAt(compressionIndex);
                return true;
            }

            if (firstUserIndex > startIndex)
            {
                messages.RemoveRange(startIndex, firstUserIndex - startIndex);
                return true;
            }

            var nextUserIndex = messages.FindIndex(
                firstUserIndex + 1,
                static message => message.Role.Label == AuthorRole.User.Label && message is not ContextCompressionChatMessage);
            if (nextUserIndex < 0)
            {
                if (compressionIndex < 0) return false;

                messages.RemoveAt(compressionIndex);
                return true;
            }

            messages.RemoveRange(firstUserIndex, nextUserIndex - firstUserIndex);
            return true;
        }
    }

    private async Task<string> RequestCompressionSummaryAsync(
        ChatContext chatContext,
        GenerationContext context,
        IReadOnlyList<ChatMessage> messages,
        ChatMessageNode compressionNode,
        CancellationToken cancellationToken)
    {
        var chatHistory = await ChatHistoryBuilder.BuildSelectedChatHistoryAsync(
            context.PromptRenderer,
            context.SystemPrompt,
            messages,
            context.InputModalities,
            cancellationToken);

        if (chatHistory.Count <= 1)
        {
            throw new ContextCompressionOutputException(
                "There is no conversation history to compact.",
                LocaleKey.ContextCompression_Error_NoHistory);
        }

        chatHistory.AddUserMessage(DefaultPrompts.ContextCompressionPrompt);

        var response = await ReadResponseAsync(
            context.KernelMixin,
            chatHistory,
            context.KernelMixin.GetPromptExecutionSettings(FunctionChoiceBehavior.None()),
            context.Kernel,
            chatContext.Metadata.Id,
            compressionNode.Id,
            StatisticsModelInvocationPurpose.ContextCompression,
            chatContext,
            compressionNode.Message,
            cancellationToken);
        if (response.FunctionCalls.Count > 0)
        {
            throw new ContextCompressionOutputException(
                "The model tried to call a tool while compacting context.",
                LocaleKey.ContextCompression_Error_ToolCallNotAllowed);
        }

        var summary = response.Text.Trim();
        if (summary.Length == 0)
        {
            throw new ContextCompressionOutputException(
                "The model returned an empty context summary.",
                LocaleKey.ContextCompression_Error_EmptyResponse);
        }

        return summary;
    }

    private static ContextCompressionTrigger? ResolvePendingAutomaticCompressionTrigger(ChatContext chatContext, int contextCompressionThreshold)
    {
        var nodes = chatContext.Items;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (nodes[i].Message is not ContextCompressionChatMessage compression) continue;
            if (compression.NeedsAutomaticCompaction) return compression.Trigger;
            break;
        }

        return chatContext.ContextUsage.Snapshot.HasReachedCompressionThreshold(contextCompressionThreshold) ?
            ContextCompressionTrigger.Automatic :
            null;
    }

    private static int ResolveCompressionBoundary(
        IReadOnlyList<ChatMessageNode> nodes,
        CompressionBoundary boundary)
    {
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(nodes[i], boundary.Node))
            {
                return boundary.ShouldIncludeNode ? i : i - 1;
            }
        }

        return -1;
    }

    private sealed record CompressionResult(bool HasAppliedSummary, Exception? Error);

    private sealed class ContextCompressionOutputException(string message, string localeKey) : Exception(message)
    {
        public string LocaleKey { get; } = localeKey;
    }
}