using Everywhere.AI.Prompts;
using Everywhere.Common;
using Everywhere.Harness;
using Everywhere.Statistics;
using Microsoft.Extensions.Logging;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Chat;

partial class ChatService
{
    private async Task<CompressionResult> CompactContextAsync(
        ChatContext chatContext,
        GenerationContext generationContext,
        ContextCompressionTrigger trigger,
        CompressionBoundary boundary,
        CancellationToken cancellationToken)
    {
        var nodes = generationContext.GetPath();
        var boundaryIndex = ResolveCompressionBoundary(nodes, boundary);
        var coveredThroughNodeId = boundaryIndex >= 0 ? nodes[boundaryIndex].Id : Guid.Empty;
        var configuration = generationContext.Session.Configuration;
        var usageBefore = chatContext.ContextUsage.Snapshot;
        var compressionMessage = new ContextCompressionChatMessage(
            coveredThroughNodeId,
            configuration.ModelId,
            trigger,
            usageBefore.TotalTokenCount,
            configuration.ContextLimit > 0 ? configuration.ContextLimit : null);

        await using var compactionScope = await chatContext.BeginContextCompactionAsync();
        var compressionNode = await chatContext.DispatchAsync(() => generationContext.Append(compressionMessage));

        try
        {
            if (boundaryIndex < 0)
            {
                throw new ContextCompressionOutputException(
                    "There is no conversation history to compact.",
                    LocaleKey.ContextCompression_Error_NoHistory);
            }

            var sourceNodes = nodes
                .AsValueEnumerable()
                .Take(boundaryIndex + 1)
                .Concat(
                    nodes
                        .AsValueEnumerable()
                        .Skip(boundaryIndex + 1)
                        .Where(static node => node.SummaryThroughNodeId.HasValue))
                .ToArray();

            var selection = await ChatHistoryBuilder.SelectContextMessagesAsync(
                chatContext,
                sourceNodes,
                generationContext.MaxContextRounds,
                configuration.ContextLimit,
                cancellationToken);
            var messages = selection.Messages.ToList();
            var wasSourceHistoryTrimmed = selection.WasHistoryTrimmed;

            string summary;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    summary = await RequestCompressionSummaryAsync(
                        chatContext,
                        generationContext,
                        messages,
                        selection.ActiveSummaryNodeId,
                        selection.WindowStartNodeId,
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
                    var handledException = ChatExceptionNormalizer.Handle(ex, generationContext.Session, new ChatRequestFailureContext(cancellationToken));
                    if (ChatExceptionNormalizer.GetFinalFailure(handledException) is not HandledChatException.InvalidRequest.ContextLengthExceeded ||
                        !TryTrimOldestConversationUnit(messages))
                    {
                        throw handledException;
                    }

                    wasSourceHistoryTrimmed = true;
                }
            }

            await chatContext.DispatchAsync(() => compressionMessage.Complete(summary, wasSourceHistoryTrimmed, DateTimeOffset.UtcNow));
            await chatContext.MarkContextCompactedAsync(configuration.ModelId, configuration.ContextLimit);
            return new CompressionResult(true, null);
        }
        catch (OperationCanceledException ex) when (IsCallerCancellation(ex, cancellationToken))
        {
            await chatContext.DispatchAsync(() => compressionMessage.Cancel(DateTimeOffset.UtcNow));
            throw;
        }
        catch (ContextCompressionOutputException ex)
        {
            await chatContext.DispatchAsync(() => compressionMessage.Fail(new DynamicLocaleKey(ex.LocaleKey), DateTimeOffset.UtcNow));
            _logger.LogWarning(ex, "Context compression returned an invalid response");
            return new CompressionResult(false, ex);
        }
        catch (Exception ex)
        {
            ex = ChatExceptionNormalizer.Handle(ex, generationContext.Session, new ChatRequestFailureContext(cancellationToken));
            await chatContext.DispatchAsync(() => compressionMessage.Fail(ex.GetFriendlyMessage(), DateTimeOffset.UtcNow));
            _logger.LogError(ex, "Failed to compact chat context");
            return new CompressionResult(false, ex);
        }

        static bool TryTrimOldestConversationUnit(List<ChatMessage> messages)
        {
            if (messages.Count <= 1) return false;

            var compressionIndex = messages.FindLastIndex(static message => message is ContextCompressionChatMessage { HasSummary: true });
            if (compressionIndex > 0)
            {
                messages.RemoveAt(0);
                return true;
            }

            var startIndex = compressionIndex + 1;
            var firstUserIndex = messages.FindIndex(
                startIndex,
                static message => message.Role == MEAI.ChatRole.User && message is not ContextCompressionChatMessage);
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
                static message => message.Role == MEAI.ChatRole.User && message is not ContextCompressionChatMessage);
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
        GenerationContext generationContext,
        IReadOnlyList<ChatMessage> messages,
        Guid? activeSummaryNodeId,
        Guid? windowStartNodeId,
        ChatMessageNode compressionNode,
        CancellationToken cancellationToken)
    {
        var chatData = chatContext.Data;
        var response = await RequestWithImageRecoveryAsync(
            chatContext,
            generationContext,
            messages,
            RequestSummaryAsync,
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

        async Task<AssembledResponse> RequestSummaryAsync(IList<MEAI.ChatMessage> history)
        {
            if (history.Count <= 1)
            {
                throw new ContextCompressionOutputException(
                    "There is no conversation history to compact.",
                    LocaleKey.ContextCompression_Error_NoHistory);
            }

            var configuration = generationContext.Session.Configuration;
            var projected = await chatContext.DispatchAsync(() => chatContext.PrefixState.ProjectReadOnly(
                history,
                new ContextPrefixIdentity(
                    generationContext.Session.Dialect,
                    configuration.ModelId,
                    configuration.SupportsToolCall,
                    activeSummaryNodeId,
                    windowStartNodeId),
                DateTimeOffset.UtcNow));

            var requestHistory = projected is not null
                ? [.. projected.History]
                : new List<MEAI.ChatMessage>(history);
            requestHistory.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, DefaultPrompts.ContextCompressionPrompt));

            var options = generationContext.Session.CreateChatOptions(
                tools: projected?.Tools,
                toolMode: MEAI.ChatToolMode.None);

            var assembled = await ReadResponseAsync(
                generationContext.Session,
                requestHistory,
                options,
                chatData.Metadata.Id,
                compressionNode.Id,
                StatisticsModelInvocationPurpose.ContextCompression,
                chatContext,
                compressionNode.Message,
                cancellationToken);
            if (assembled.FunctionCalls.Count == 0)
            {
                return assembled;
            }

            // Allow one corrective retry per unexpected tool-call batch while keeping the cached prefix intact.
            requestHistory.Add(assembled.Message);
            requestHistory.Add(BuildToolCallRejectionMessage(assembled.FunctionCalls));

            return await ReadResponseAsync(
                generationContext.Session,
                requestHistory,
                options,
                chatData.Metadata.Id,
                compressionNode.Id,
                StatisticsModelInvocationPurpose.ContextCompression,
                chatContext,
                compressionNode.Message,
                cancellationToken);
        }

        static MEAI.ChatMessage BuildToolCallRejectionMessage(IReadOnlyList<MEAI.FunctionCallContent> functionCalls)
        {
            var contents = new List<MEAI.AIContent>(functionCalls.Count);
            foreach (var call in functionCalls)
            {
                contents.Add(
                    new MEAI.FunctionResultContent(
                        call.CallId,
                        "Error: Tool calls are disabled during context compression. Do not call any tools; reply only with the checkpoint summary."));
            }

            return new MEAI.ChatMessage(MEAI.ChatRole.Tool, contents);
        }
    }

    private static async Task<ContextCompressionTrigger?> ResolvePendingAutomaticCompressionTriggerAsync(
        ChatContext chatContext,
        int contextCompressionThreshold,
        CancellationToken cancellationToken)
    {
        var chatData = chatContext.Data;
        var nodes = chatContext.GenerationContext?.GetPath() ?? chatData.SelectedNodes;
        for (var i = nodes.Count - 1; i >= 0; i--)
        {
            var node = nodes[i];
            if (node.MessageKind != ChatMessageKind.Compression)
            {
                continue;
            }

            if (node.Turn is { } turn)
            {
                await chatContext.EnsureTurnLoadedAsync(turn, cancellationToken);
            }
            else
            {
                await chatContext.EnsureTurnLoadedAsync(null, cancellationToken);
            }

            var compression = (ContextCompressionChatMessage)node.Message;
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