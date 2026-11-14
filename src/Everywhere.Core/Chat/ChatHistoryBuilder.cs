using System.Security;
using Everywhere.AI;
using Everywhere.Common;
using Everywhere.Prompting;
using Everywhere.Prompting.Documents;
using Everywhere.Utilities;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;

namespace Everywhere.Chat;

/// <summary>
/// Builds ChatHistory (SK) from ChatMessages (Everywhere).
/// </summary>
public static partial class ChatHistoryBuilder
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

    /// <summary>
    /// Captures provider input in the owning execution domain, then reads only captured attachment
    /// paths. Detached message consumers may omit the context when they own their input exclusively.
    /// </summary>
    public static async ValueTask<ChatHistory> BuildSelectedChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> selectedMessages,
        Modalities supportedModalities,
        CancellationToken cancellationToken = default,
        ChatContext? context = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (chatHistory, pendingFiles) = context is null ? Capture() : await context.DispatchAsync(Capture).ConfigureAwait(false);

        (ChatHistory History, List<PendingFileContent> Files) Capture()
        {
            var history = new ChatHistory();
            history.AddSystemMessage(systemPrompt);
            var files = new List<PendingFileContent>();
            foreach (var message in selectedMessages)
            {
                history.AddRange(CaptureChatMessageContents(promptRenderer, message, files));
            }

            return (history, files);
        }

        foreach (var pending in pendingFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var contents = new ChatMessageContentItemCollection();
            if (pending.IsGeneratedImage)
            {
                try
                {
                    var data = await File.ReadAllBytesAsync(pending.File.FilePath, cancellationToken);
                    contents.Add(new ImageContent(data, pending.File.MimeType) { Metadata = pending.Metadata });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    contents.Add(new TextContent("The image is generated but failed to be read from disk.", metadata: pending.Metadata));
                }
            }
            else
            {
                await ReadFileContentsAsync(pending.File, contents, supportedModalities, cancellationToken);
            }

            var index = pending.Target.IndexOf(pending.Placeholder);
            pending.Target.RemoveAt(index);
            foreach (var content in contents)
            {
                pending.Target.Insert(index++, content);
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
        return SelectContextMessages(chatNodes, maxContextRounds, declaredContextLimit, out _);
    }

    /// <summary>
    /// Selects the active summary, optional retained user text, and the uncompressed suffix.
    /// Reports whether the round limit omitted history from this selection, so compression can
    /// distinguish a checkpoint of the active context from a summary of the entire prefix.
    /// </summary>
    public static IReadOnlyList<ChatMessage> SelectContextMessages(
        IReadOnlyList<ChatMessageNode> chatNodes,
        int maxContextRounds,
        int declaredContextLimit,
        out bool wasHistoryTrimmed)
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
            wasHistoryTrimmed = startIndex > 0;
            return messages.AsValueEnumerable().Skip(startIndex).ToArray();
        }

        var suffix = chatNodes
            .AsValueEnumerable()
            .Skip(anchorIndex + 1)
            .Select(static node => node.Message)
            .Where(static message => message is not ContextCompressionChatMessage and not RootChatMessage)
            .ToArray();
        var suffixStartIndex = ResolveStartIndex(suffix, maxContextRounds, 0);
        wasHistoryTrimmed = suffixStartIndex > 0;
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
    /// <param name="chatMessage"></param>
    /// <param name="pendingFiles"></param>
    /// <returns></returns>
    private static IEnumerable<ChatMessageContent> CaptureChatMessageContents(
        IPromptRenderer promptRenderer,
        ChatMessage chatMessage,
        List<PendingFileContent> pendingFiles)
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
                            items.AddRange(functionCalls.SelectMany(function => function.Calls).Select(CaptureFunctionCall));

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
                                    var persistedResult = resultContent?.Result;
                                    var modelResult = persistedResult is ChatFunctionResult structuredResult ? structuredResult.Value : persistedResult;
                                    if (resultContent is not null)
                                    {
                                        // Render prompt nodes now; neither the result container nor a mutable
                                        // attachment may be read again after asynchronous file loading begins.
                                        var capturedResult = modelResult switch
                                        {
                                            PromptNode promptNode => promptNode.ToString(),
                                            FileAttachment file => CaptureFile(file),
                                            _ => modelResult,
                                        };
                                        resultItems.Add(
                                            new FunctionResultContent(
                                                resultContent.FunctionName,
                                                resultContent.PluginName,
                                                resultContent.CallId,
                                                capturedResult)
                                            {
                                                Metadata = resultContent.Metadata?.ToDictionary(),
                                                InnerContent = resultContent.InnerContent
                                            });
                                    }
                                    else
                                    {
                                        resultItems.Add(new FunctionResultContent(
                                            call,
                                            $"Error: No result found for function call ID '{callId}'. " +
                                            "This may caused by an error during function execution or user cancellation."));
                                    }

                                    // If the function call result owns ChatAttachments, add them as extra attachment message(s).
                                    if (persistedResult is ChatAttachment extraToolCallResult)
                                    {
                                        extraToolCallResults.Add(extraToolCallResult);
                                    }
                                    else if (persistedResult is ChatFunctionResult structuredAttachmentResult)
                                    {
                                        extraToolCallResults.AddRange(structuredAttachmentResult.Attachments);
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
                                    CaptureAttachment(extraToolCallResult, attachmentItems, pendingFiles);
                                }

                                // No valid attachment added, do nothing
                                if (attachmentItems.Count == 1) break;

                                attachmentItems.Add(new TextContent("</ExtraToolCallResultAttachments>"));
                                yield return new ChatMessageContent(AuthorRole.User, attachmentItems);
                            }

                            break;
                        }
                        case AssistantChatMessageVisualContextResetSpan resetSpan:
                        {
                            if (items.Count > 0)
                            {
                                yield return new ChatMessageContent(AuthorRole.Assistant, items, metadata: assistantChatMessage.Metadata);
                                items = [];
                            }

                            yield return new ChatMessageContent(resetSpan.Message.Role, resetSpan.Message.Content);
                            break;
                        }
                        case AssistantChatMessageReasoningSpan { ReasoningOutput: { Length: > 0 } reasoningOutput }:
                        {
                            items.Add(new ReasoningContent(reasoningOutput) { Metadata = span.Metadata });
                            break;
                        }
                        case AssistantChatMessageImageSpan { ImageOutput: { } imageOutput }:
                        {
                            AddPendingFile(imageOutput, items, pendingFiles, isGeneratedImage: true, span.Metadata);
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
                    CaptureAttachment(chatAttachment, items, pendingFiles);
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

    private static FunctionCallContent CaptureFunctionCall(FunctionCallContent call)
    {
        return new FunctionCallContent(
            call.FunctionName,
            call.PluginName,
            call.Id,
            call.Arguments is null ? null : new KernelArguments(call.Arguments))
        {
            Metadata = call.Metadata?.ToDictionary(),
            InnerContent = call.InnerContent,
        };
    }

    private static FileAttachment CaptureFile(FileAttachment file)
    {
        return new FileAttachment(file.HeaderKey, file.FilePath, file.Sha256, file.MimeType, file.Description);
    }

    private static void CaptureAttachment(
        ChatAttachment chatAttachment,
        ChatMessageContentItemCollection contents,
        List<PendingFileContent> pendingFiles)
    {
        switch (chatAttachment)
        {
            case TextSelectionAttachment textSelection:
            {
                var incompleteAttribute = textSelection.IsTextIncomplete ? " incomplete=\"true\"" : string.Empty;
                contents.Add(
                    new TextContent(
                        $"""
                         <Attachment type="text-selection"{incompleteAttribute}>
                         <Text>
                         {textSelection.Text}
                         </Text>
                         <AssociatedElement>
                         {textSelection.Content?.ToString() ?? "omitted due to duplicate"}
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
                         {visualElement.Content?.ToString() ?? "omitted due to duplicate"}
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
                AddPendingFile(file, contents, pendingFiles, isGeneratedImage: false, MetadataDictionary.Empty);
                break;
            }
        }
    }

    private static void AddPendingFile(
        FileAttachment file,
        ChatMessageContentItemCollection target,
        List<PendingFileContent> pendingFiles,
        bool isGeneratedImage,
        MetadataDictionary metadata)
    {
        var placeholder = new TextContent(string.Empty);
        target.Add(placeholder);
        pendingFiles.Add(new PendingFileContent(target, placeholder, CaptureFile(file), isGeneratedImage, metadata));
    }

    private static async ValueTask ReadFileContentsAsync(
        FileAttachment file,
        ChatMessageContentItemCollection contents,
        Modalities supportedModalities,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(file.FilePath);
        if (!fileInfo.Exists)
        {
            contents.Add(GetOmittedContent("file not found"));
            return;
        }
        if (fileInfo.Length == 0)
        {
            contents.Add(GetOmittedContent("file is empty"));
            return;
        }
        if (fileInfo.Length > FileAttachment.MaximumInlineContentSizeInBytes)
        {
            contents.Add(
                GetOmittedContent(
                    $"file size {Humanizer.HumanizeBytes(fileInfo.Length)} exceeds the maximum supported size " +
                    Humanizer.HumanizeBytes(FileAttachment.MaximumInlineContentSizeInBytes)));
            return;
        }
        if (!supportedModalities.SupportsMimeType(file.MimeType))
        {
            contents.Add(GetOmittedContent("file modality is unsupported, try process with tool if any. e.g. `run_subagent`"));
            return;
        }

        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Keep the reference in the prompt even when the content cannot be read.
            ex = HandledSystemException.Handle(ex, true); // treat all as expected
            Log.ForContext(typeof(ChatHistoryBuilder)).Warning(ex, "Failed to read attachment file '{FilePath}'", file.FilePath);
            contents.Add(GetOmittedContent("file could not be read"));
            return;
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
        return;

        TextContent GetOmittedContent(string reason) => new(
            $"""
             <Attachment type="file" path="{SecurityElement.Escape(file.FilePath)}" mimeType="{SecurityElement.Escape(file.MimeType)}" description="{SecurityElement.Escape(file.Description)}">
             Content omitted because {reason}
             </Attachment>
             """);
    }

    // These placeholders are private to request construction and are all expanded before return.
    // Keeping the target collection preserves the existing ordering of text, tools and attachments.
    private sealed record PendingFileContent(
        ChatMessageContentItemCollection Target,
        TextContent Placeholder,
        FileAttachment File,
        bool IsGeneratedImage,
        MetadataDictionary Metadata);
}