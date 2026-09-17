using System.Security;
using Everywhere.AI;
using Everywhere.Chat.Documents;
using Everywhere.Common;
using Everywhere.Utilities;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;

namespace Everywhere.Chat;

/// <summary>
/// Builds ChatHistory (SK) from ChatMessages (Everywhere).
/// </summary>
public static class ChatHistoryBuilder
{
    // Retain useful recent user instructions after compression while bounding their contribution
    // to the next prompt. The absolute cap and context-relative cap trade off continuity against
    // keeping enough room for the system prompt, tools, summary, and the next response.
    private const int MaximumRetainedUserMessageTokens = 20_000;
    private const double RetainedUserMessageContextRatio = 0.1d;
    private const string RetainedUserMessageOmissionMarker = "[... older content omitted from retained user message ...]";

    public static async ValueTask<ChatHistory> BuildChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> chatMessages,
        int maxContextRounds,
        Modalities supportedModalities,
        CancellationToken cancellationToken = default)
    {
        var selectedMessages = SelectContextMessages(chatMessages, maxContextRounds);
        return await BuildSelectedChatHistoryAsync(
            promptRenderer,
            systemPrompt,
            selectedMessages,
            supportedModalities,
            cancellationToken);
    }

    public static async ValueTask<ChatHistory> BuildChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessageNode> chatNodes,
        int maxContextRounds,
        Modalities supportedModalities,
        int declaredContextLimit,
        CancellationToken cancellationToken = default)
    {
        var selectedMessages = SelectContextMessages(chatNodes, maxContextRounds, declaredContextLimit);
        return await BuildSelectedChatHistoryAsync(
            promptRenderer,
            systemPrompt,
            selectedMessages,
            supportedModalities,
            cancellationToken);
    }

    public static async ValueTask<ChatHistory> BuildSelectedChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> selectedMessages,
        Modalities supportedModalities,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(systemPrompt);

        foreach (var chatMessage in selectedMessages)
        {
            await foreach (var chatMessageContent in CreateChatMessageContentsAsync(
                               promptRenderer,
                               chatMessage,
                               supportedModalities,
                               cancellationToken))
            {
                chatHistory.Add(chatMessageContent);
            }
        }

        return chatHistory;
    }

    public static IReadOnlyList<ChatMessage> SelectContextMessages(
        IReadOnlyList<ChatMessage> chatMessages,
        int maxContextRounds)
    {
        var compressionIndex = FindLastSuccessfulCompressionIndex(chatMessages);
        if (compressionIndex < 0)
        {
            var startIndex = ResolveStartIndex(chatMessages, maxContextRounds, 0);
            return chatMessages
                .Skip(startIndex)
                .Where(static message => message is not ContextCompressionChatMessage and not RootChatMessage)
                .ToArray();
        }

        var suffixStartIndex = ResolveStartIndex(chatMessages, maxContextRounds, compressionIndex + 1);
        var result = new List<ChatMessage>(chatMessages.Count - suffixStartIndex + 1)
        {
            chatMessages[compressionIndex]
        };
        result.AddRange(
            chatMessages
                .Skip(suffixStartIndex)
                .Where(static message => message is not ContextCompressionChatMessage and not RootChatMessage));
        return result;
    }

    public static IReadOnlyList<ChatMessage> SelectContextMessages(
        IReadOnlyList<ChatMessageNode> chatNodes,
        int maxContextRounds,
        int declaredContextLimit)
    {
        var compressionIndex = FindLastSuccessfulCompressionIndex(chatNodes, out var anchorIndex);
        if (compressionIndex < 0)
        {
            var messages = chatNodes
                .AsValueEnumerable()
                .Select(static node => node.Message)
                .Where(static message => message is not ContextCompressionChatMessage and not RootChatMessage)
                .ToArray();
            var startIndex = ResolveStartIndex(messages, maxContextRounds, 0);
            return messages.AsValueEnumerable().Skip(startIndex).ToArray();
        }

        var suffix = chatNodes
            .AsValueEnumerable()
            .Skip(anchorIndex + 1)
            .Select(static node => node.Message)
            .Where(static message => message is not ContextCompressionChatMessage and not RootChatMessage)
            .ToArray();
        var suffixStartIndex = ResolveStartIndex(suffix, maxContextRounds, 0);
        var retainedUserMessages = SelectRetainedUserMessages(chatNodes, anchorIndex, declaredContextLimit);
        var result = new List<ChatMessage>(retainedUserMessages.Count + suffix.Length - suffixStartIndex + 1);
        result.AddRange(retainedUserMessages);
        result.Add(chatNodes[compressionIndex].Message);
        result.AddRange(suffix.Skip(suffixStartIndex));
        return result;
    }

    private static int FindLastSuccessfulCompressionIndex(IReadOnlyList<ChatMessage> chatMessages)
    {
        for (var i = chatMessages.Count - 1; i >= 0; i--)
        {
            if (chatMessages[i] is ContextCompressionChatMessage { HasSummary: true }) return i;
        }

        return -1;
    }

    private static int FindLastSuccessfulCompressionIndex(IReadOnlyList<ChatMessageNode> chatNodes, out int anchorIndex)
    {
        for (var compressionIndex = chatNodes.Count - 1; compressionIndex >= 0; compressionIndex--)
        {
            if (chatNodes[compressionIndex].Message is not ContextCompressionChatMessage { HasSummary: true } compression)
            {
                continue;
            }

            anchorIndex = compression.CoveredThroughNodeId == Guid.Empty ? -1 : FindNodeIndex(chatNodes, compression.CoveredThroughNodeId);
            if (compression.CoveredThroughNodeId != Guid.Empty && anchorIndex < 0) continue;
            if (anchorIndex < compressionIndex) return compressionIndex;
        }

        anchorIndex = -1;
        return -1;
    }

    private static int FindNodeIndex(IReadOnlyList<ChatMessageNode> chatNodes, Guid nodeId)
    {
        for (var i = 0; i < chatNodes.Count; i++)
        {
            if (chatNodes[i].Id == nodeId) return i;
        }

        return -1;
    }

    private static List<ChatMessage> SelectRetainedUserMessages(IReadOnlyList<ChatMessageNode> chatNodes, int anchorIndex, int declaredContextLimit)
    {
        var tokenBudget = declaredContextLimit > 0 ?
            Math.Min(MaximumRetainedUserMessageTokens, (int)(declaredContextLimit * RetainedUserMessageContextRatio)) :
            0;
        if (tokenBudget <= 0 || anchorIndex < 0) return [];

        var remainingTokens = tokenBudget;
        var retainedMessages = new List<ChatMessage>();
        for (var i = anchorIndex; i >= 0 && remainingTokens > 0; i--)
        {
            if (chatNodes[i].Message is not UserChatMessage userMessage) continue;

            var estimatedTokens = TokenHelper.EstimateTokenCount(userMessage.Content);
            if (estimatedTokens <= remainingTokens)
            {
                retainedMessages.Add(new UserChatMessage(userMessage.Content, []));
                remainingTokens -= estimatedTokens;
                continue;
            }

            var retainedContent = TokenHelper.Omit(userMessage.Content, remainingTokens, RetainedUserMessageOmissionMarker);
            if (!string.IsNullOrWhiteSpace(retainedContent))
            {
                retainedMessages.Add(new UserChatMessage(retainedContent, []));
            }
            break;
        }

        retainedMessages.Reverse();
        return retainedMessages;
    }

    private static int ResolveStartIndex(IReadOnlyList<ChatMessage> chatMessages, int maxContextRounds, int minimumIndex)
    {
        if (chatMessages.Count == 0 || maxContextRounds <= -1)
        {
            return minimumIndex;
        }

        var matchedUserRounds = 0;

        for (var i = chatMessages.Count - 1; i >= minimumIndex; i--)
        {
            if (chatMessages[i].Role != AuthorRole.User)
            {
                continue;
            }

            matchedUserRounds++;
            if (matchedUserRounds - 1 == maxContextRounds)
            {
                return i;
            }
        }

        return minimumIndex;
    }

    /// <summary>
    /// Creates chat message contents from a chat message.
    /// </summary>
    /// <param name="promptRenderer"></param>
    /// <param name="supportedModalities"></param>
    /// <param name="chatMessage"></param>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    private static async IAsyncEnumerable<ChatMessageContent> CreateChatMessageContentsAsync(
        IPromptRenderer promptRenderer,
        ChatMessage chatMessage,
        Modalities supportedModalities,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        switch (chatMessage)
        {
            case AssistantChatMessage assistantChatMessage:
            {
                var items = new ChatMessageContentItemCollection();
                foreach (var span in assistantChatMessage.Items)
                {
                    switch (span)
                    {
                        case AssistantChatMessageTextSpan { Content: { Length: > 0 } content }:
                        {
                            items.Add(new TextContent(content, metadata: span.Metadata));
                            break;
                        }
                        case AssistantChatMessageFunctionCallSpan { Items: { Count: > 0 } functionCalls }:
                        {
                            // 1. Add all function calls as content items.
                            items.AddRange(functionCalls.SelectMany(f => f.Calls));

                            // 2. Yield the assistant message with function call items first
                            yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: assistantChatMessage.Metadata);
                            items = [];

                            // 3. Yield the function call results as separate tool messages
                            var resultItems = new ChatMessageContentItemCollection();
                            var extraToolCallResults = new List<ChatAttachment>();
                            foreach (var functionCall in functionCalls)
                            {
                                foreach (var call in functionCall.Calls)
                                {
                                    var callId = call.Id;
                                    if (callId.IsNullOrEmpty())
                                    {
                                        throw new InvalidOperationException("Function CallId cannot be null or empty.");
                                    }

                                    var resultContent = functionCall.Results.AsValueEnumerable().FirstOrDefault(r => r.CallId == callId);
                                    if (resultContent?.Result is PromptNode promptNode)
                                    {
                                        // Preserve the node in chat history and render only the temporary
                                        // provider-facing copy, including any declared local token limit.
                                        resultItems.Add(
                                            new FunctionResultContent(
                                                resultContent.FunctionName,
                                                resultContent.PluginName,
                                                resultContent.CallId,
                                                promptNode.ToString())
                                            {
                                                Metadata = resultContent.Metadata,
                                                InnerContent = resultContent.InnerContent
                                            });
                                    }
                                    else
                                    {
                                        resultItems.Add(
                                            resultContent ?? new FunctionResultContent(
                                                call,
                                                $"Error: No result found for function call ID '{callId}'. " +
                                                $"This may caused by an error during function execution or user cancellation."));
                                    }

                                    // If the function call result is a ChatAttachment, add it as extra attachment message(s).
                                    if (resultContent?.Result is ChatAttachment extraToolCallResult)
                                    {
                                        extraToolCallResults.Add(extraToolCallResult);
                                    }
                                }
                            }

                            yield return new ChatMessageContent(AuthorRole.Tool, resultItems);

                            // 4. Workaround for any function call results that are ChatAttachments
                            // We put them as user message because tool message doesn't support attachments
                            if (extraToolCallResults.Count > 0)
                            {
                                var attachmentItems = new ChatMessageContentItemCollection { new TextContent("<ExtraToolCallResultAttachments>") };
                                foreach (var extraToolCallResult in extraToolCallResults)
                                {
                                    await PopulateKernelContentsAsync(extraToolCallResult, attachmentItems, supportedModalities, cancellationToken);
                                }

                                // No valid attachment added, do nothing
                                if (attachmentItems.Count == 1) break;

                                attachmentItems.Add(new TextContent("</ExtraToolCallResultAttachments>"));
                                yield return new ChatMessageContent(AuthorRole.User, attachmentItems);
                            }

                            break;
                        }
                        case AssistantChatMessageReasoningSpan { ReasoningOutput: { Length: > 0 } reasoningOutput }:
                        {
                            items.Add(new ReasoningContent(reasoningOutput) { Metadata = span.Metadata });
                            break;
                        }
                        case AssistantChatMessageImageSpan { ImageOutput: { } imageOutput }:
                        {
                            try
                            {
                                var imageData = await File.ReadAllBytesAsync(imageOutput.FilePath, cancellationToken);
                                items.Add(
                                    new ImageContent(imageData, imageOutput.MimeType)
                                    {
                                        Metadata = span.Metadata
                                    });
                            }
                            catch
                            {
                                items.Add(new TextContent("The image is generated but failed to be read from disk.", metadata: span.Metadata));
                            }
                            break;
                        }
                    }
                }

                if (items.Count > 0)
                {
                    yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: assistantChatMessage.Metadata);
                }
                break;
            }
            case UserChatMessage userChatMessage:
            {
                var items = new ChatMessageContentItemCollection();
                foreach (var chatAttachment in userChatMessage.Attachments.AsValueEnumerable().ToArray())
                {
                    await PopulateKernelContentsAsync(chatAttachment, items, supportedModalities, cancellationToken);
                }

                if (userChatMessage is UserStrategyChatMessage { Strategy.Body: { Length: > 0 } strategyBody } userStrategyMessage)
                {
                    // If UserMessage template is provided, render the content with the template.
                    var renderedContent = promptRenderer.RenderStrategyUserPrompt(
                        strategyBody,
                        userChatMessage.Content,
                        userStrategyMessage.PreprocessorResult);
                    items.Add(new TextContent(renderedContent));
                }
                else
                {
                    if (userChatMessage.Content.Length > 0)
                    {
                        items.Add(new TextContent(userChatMessage.Content));
                    }
                }

                yield return new ChatMessageContent(AuthorRole.User, items);
                break;
            }
            case ContextCompressionChatMessage { HasSummary: true } compression:
            {
                yield return new ChatMessageContent(AuthorRole.User, compression.ToString());
                break;
            }
            case { Role.Label: "system" or "user" or "developer" or "tool" } when chatMessage.ToString() is { Length: > 0 } content:
            {
                yield return new ChatMessageContent(chatMessage.Role, content);
                break;
            }
        }
    }

    /// <summary>
    /// Creates KernelContent from a chat attachment, and adds them to the contents list.
    /// </summary>
    /// <param name="chatAttachment"></param>
    /// <param name="contents"></param>
    /// <param name="supportedModalities"></param>
    /// <param name="cancellationToken"></param>
    private static async ValueTask PopulateKernelContentsAsync(
        ChatAttachment chatAttachment,
        ChatMessageContentItemCollection contents,
        Modalities supportedModalities,
        CancellationToken cancellationToken)
    {
        switch (chatAttachment)
        {
            case TextSelectionAttachment textSelection:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text-selection">
                         <Text>
                         {textSelection.Text}
                         </Text>
                         <AssociatedElement>
                         {textSelection.Content ?? "omitted due to duplicate"}
                         </AssociatedElement>
                         </Attachment>
                         """));
                break;
            }
            case VisualElementAttachment visualElement:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="visual-element">
                         {visualElement.Content ?? "omitted due to duplicate"}
                         </Attachment>
                         """));
                break;
            }
            case TextAttachment text:
            {
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text">
                         {text}
                         </Attachment>
                         """));
                break;
            }
            case FileAttachment file:
            {
                var fileInfo = new FileInfo(file.FilePath);
                if (!fileInfo.Exists)
                {
                    contents.Add(GetOmittedContent("file not found"));
                    break;
                }
                if (fileInfo.Length == 0)
                {
                    contents.Add(GetOmittedContent("file is empty"));
                    break;
                }
                if (fileInfo.Length > FileAttachment.MaximumInlineContentSizeInBytes)
                {
                    contents.Add(
                        GetOmittedContent(
                            $"file size {Humanizer.HumanizeBytes(fileInfo.Length)} exceeds the maximum supported size " +
                            Humanizer.HumanizeBytes(FileAttachment.MaximumInlineContentSizeInBytes)));
                    break;
                }
                if (!supportedModalities.SupportsMimeType(file.MimeType))
                {
                    contents.Add(GetOmittedContent("file modality is unsupported, try process with tool if any. e.g. `run_subagent`"));
                    break;
                }

                byte[] data;
                try
                {
                    data = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
                }
                catch (Exception ex)
                {
                    // Keep the reference in the prompt even when the content cannot be read.
                    ex = HandledSystemException.Handle(ex, true); // treat all as expected
                    Log.ForContext(typeof(ChatHistoryBuilder)).Warning(ex, "Failed to read attachment file '{FilePath}'", file.FilePath);
                    contents.Add(GetOmittedContent("file could not be read"));
                    break;
                }

                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="file" path="{SecurityElement.Escape(file.FilePath)}" mimeType="{SecurityElement.Escape(file.MimeType)}" description="{SecurityElement.Escape(file.Description)}">
                         """));
                contents.Add(
                    FileUtilities.GetCategory(file.MimeType) switch
                    {
                        FileTypeCategory.Audio => new AudioContent(data, file.MimeType),
                        FileTypeCategory.Image => new ImageContent(data, file.MimeType),
                        _ => new BinaryContent(data, file.MimeType)
                    });
                contents.Add(new TextContent("</Attachment>"));
                break;

                TextContent GetOmittedContent(string reason) => new(
                    $"""
                     <Attachment type="file" path="{SecurityElement.Escape(file.FilePath)}" mimeType="{SecurityElement.Escape(file.MimeType)}" description="{SecurityElement.Escape(file.Description)}">
                     Content omitted because {reason}
                     </Attachment>
                     """);
            }
        }
    }
}