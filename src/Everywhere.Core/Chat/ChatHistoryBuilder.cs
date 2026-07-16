using System.Security;
using Everywhere.AI;
using Everywhere.Chat.Documents;
using Everywhere.Common;
using Everywhere.Utilities;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Serilog;
using ZLinq;

namespace Everywhere.Chat;

/// <summary>
/// Builds ChatHistory (SK) from ChatMessages (Everywhere).
/// </summary>
public static class ChatHistoryBuilder
{
    public static async ValueTask<ChatHistory> BuildChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> chatMessages,
        int maxContextRounds,
        Modalities supportedModalities,
        CancellationToken cancellationToken = default)
    {
        var chatHistory = new ChatHistory();
        chatHistory.AddSystemMessage(systemPrompt);

        var startIndex = ResolveStartIndex(chatMessages, maxContextRounds);

        foreach (var chatMessage in chatMessages.Skip(startIndex))
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

    private static int ResolveStartIndex(IReadOnlyList<ChatMessage> chatMessages, int maxContextRounds)
    {
        if (chatMessages.Count == 0 || maxContextRounds <= -1)
        {
            return 0;
        }

        var matchedUserRounds = 0;

        for (var i = chatMessages.Count - 1; i >= 0; i--)
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

        return 0;
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
                                        resultItems.Add(new FunctionResultContent(
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
                foreach (var chatAttachment in userChatMessage.Attachments.AsValueEnumerable().ToList())
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
                    // No attachments, just add the content directly.
                    items.Add(new TextContent(userChatMessage.Content));
                }

                yield return new ChatMessageContent(AuthorRole.User, items);
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
                if (fileInfo.Length > 25 * 1024 * 1024) // TODO: Configurable max file size?
                {
                    contents.Add(GetOmittedContent($"file size {fileInfo.Length} exceeds the maximum supported size 25MB"));
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
                    await using var stream = fileInfo.OpenRead();
                    data = await File.ReadAllBytesAsync(file.FilePath, cancellationToken);
                }
                catch (Exception ex)
                {
                    // If we fail to read the file, just skip it.
                    // The file might be deleted or moved.
                    // We don't want to fail the whole message because of one attachment.
                    // Just log the error and continue.
                    ex = HandledSystemException.Handle(ex, true); // treat all as expected
                    Log.ForContext(typeof(ChatHistoryBuilder)).Warning(ex, "Failed to read attachment file '{FilePath}'", file.FilePath);
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