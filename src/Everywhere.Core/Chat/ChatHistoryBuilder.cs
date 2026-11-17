using System.Security;
using Everywhere.AI;
using Everywhere.Harness;
using Everywhere.Prompting;
using Everywhere.Prompting.Documents;
using Serilog;
using MEAI = Microsoft.Extensions.AI;

namespace Everywhere.Chat;

/// <summary>
/// Builds MEAI <see cref="MEAI.ChatMessage"/> sequences from durable <see cref="ChatMessage"/> histories.
/// </summary>
public static partial class ChatHistoryBuilder
{
    // Retain useful recent user instructions after compression while bounding their contribution
    // to the next prompt. The absolute cap and context-relative cap trade off continuity against
    // keeping enough room for the system prompt, tools, summary, and the next response.
    private const int MaximumRetainedUserMessageTokens = 20_000;
    private const double RetainedUserMessageContextRatio = 0.1d;
    private const string RetainedUserMessageOmissionMarker = "[... older content omitted from retained user message ...]";

    public static async ValueTask<IList<MEAI.ChatMessage>> BuildChatHistoryAsync(
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

    public static async ValueTask<IList<MEAI.ChatMessage>> BuildChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessageNode> chatNodes,
        int maxContextRounds,
        Modalities supportedModalities,
        int declaredContextLimit,
        CancellationToken cancellationToken = default)
    {
        var selectedMessages = SelectContextMessages(chatNodes, maxContextRounds, declaredContextLimit);
        var offloadedImages = chatNodes
            .AsValueEnumerable()
            .Select(node => node.Message)
            .OfType<ChatImageOffloadMessage>()
            .SelectMany(message => message.Images)
            .ToHashSet();
        var input = await BuildModelInputAsync(
            promptRenderer,
            systemPrompt,
            selectedMessages,
            supportedModalities,
            offloadedImages,
            cancellationToken);
        return input.History;
    }

    /// <summary>
    /// Captures provider input in the owning execution domain, then reads only captured attachment
    /// paths. Detached message consumers may omit the context when they own their input exclusively.
    /// </summary>
    public static async ValueTask<IList<MEAI.ChatMessage>> BuildSelectedChatHistoryAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> selectedMessages,
        Modalities supportedModalities,
        CancellationToken cancellationToken = default,
        ChatContext? context = null)
    {
        var input = await BuildModelInputAsync(
            promptRenderer,
            systemPrompt,
            selectedMessages,
            supportedModalities,
            offloadedImages: null,
            cancellationToken,
            context);
        return input.History;
    }

    public static async ValueTask<ChatModelInput> BuildModelInputAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> selectedMessages,
        Modalities supportedModalities,
        IReadOnlySet<ChatImageOccurrence>? offloadedImages,
        CancellationToken cancellationToken,
        ChatContext? context = null)
    {
        return await CaptureModelInputAsync(
            promptRenderer,
            systemPrompt,
            selectedMessages,
            supportedModalities,
            offloadedImages,
            cancellationToken,
            context);
    }

    /// <summary>
    /// Captures provider-ready MEAI messages and image occurrences directly from durable conversation spans,
    /// scoping cryptographic reasoning continuation metadata to matching dialects.
    /// </summary>
    public static async ValueTask<ChatModelInput> CaptureModelInputAsync(
        IPromptRenderer promptRenderer,
        string systemPrompt,
        IReadOnlyList<ChatMessage> selectedMessages,
        Modalities supportedModalities,
        IReadOnlySet<ChatImageOccurrence>? offloadedImages,
        CancellationToken cancellationToken,
        ChatContext? context = null,
        string? targetDialect = null,
        bool supportsToolResultBlocks = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (chatHistory, pendingImages) = context is null ? Capture() : await context.DispatchAsync(Capture).ConfigureAwait(false);

        var sent = new List<ChatImageOccurrence>(pendingImages.Count);
        foreach (var media in pendingImages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (media is not PendingImage pending)
            {
                sent.Add(media.Occurrence);
                continue;
            }

            try
            {
                var prepared = pending.Image;
                byte[] bytes;
                var mimeType = prepared.MimeType;
                var originalWidth = pending.OriginalWidth;
                var originalHeight = pending.OriginalHeight;
                if (prepared.IsSnapshot)
                {
                    await using var stream = File.OpenRead(prepared.Path);
                    var length = stream.Length;
                    if (length is <= 0 or > ChatImage.MaximumEncodedBytes)
                    {
                        throw new InvalidDataException("The stored image is empty or exceeds the prepared image size limit.");
                    }

                    bytes = new byte[(int)length];
                    await stream.ReadExactlyAsync(bytes, cancellationToken);
                }
                else
                {
                    var image = ChatImage.Read(prepared.Path);
                    bytes = image.Bytes;
                    mimeType = image.MimeType;
                    if (image.Width != image.OriginalWidth || image.Height != image.OriginalHeight)
                    {
                        originalWidth = image.OriginalWidth;
                        originalHeight = image.OriginalHeight;
                    }

                    prepared = prepared with { MimeType = mimeType, Width = image.Width, Height = image.Height };
                }

                pending.Provide(DescribeImage(prepared, originalWidth, originalHeight, "Image follows."), bytes, mimeType);
                sent.Add(pending.Occurrence);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                pending.Omit(DescribeImage(pending.Image, pending.OriginalWidth, pending.OriginalHeight, "Image not available."));
                Log.ForContext(typeof(ChatHistoryBuilder)).Debug(error, "Could not prepare contextual image {Path}", pending.Image.Path);
            }
        }

        return new ChatModelInput(chatHistory, sent);

        (List<MEAI.ChatMessage> History, List<PendingMedia> Images) Capture()
        {
            var history = new List<MEAI.ChatMessage>
            {
                new(MEAI.ChatRole.System, systemPrompt)
            };
            var images = new List<PendingMedia>();
            foreach (var message in selectedMessages)
            {
                history.AddRange(
                    CaptureChatMessageContents(
                        promptRenderer,
                        message,
                        supportedModalities.SupportsImage,
                        offloadedImages,
                        images,
                        targetDialect,
                        supportsToolResultBlocks));
            }

            return (history, images);
        }
    }

    /// <summary>
    /// Reconstructs a <see cref="MEAI.TextReasoningContent"/> from span text and persisted <see cref="ReasoningMetadata"/>.
    /// </summary>
    internal static MEAI.TextReasoningContent? CreateReasoningContent(
        string? text,
        MetadataDictionary metadata,
        string? targetDialect = null)
    {
        var spanDialect = metadata.TryGetValue(ReasoningMetadata.DialectKey, out var rawDialect) ? rawDialect as string : null;
        var protectedData = metadata.TryGetValue(ReasoningMetadata.ProtectedDataKey, out var rawProtected) ? rawProtected as string : null;
        if (protectedData is null && metadata.TryGetValue("ProtectedData", out var legacyProtected) &&
            legacyProtected is string { Length: > 0 } legacyValue)
        {
            // Backward compatibility for Anthropic reasoning spans persisted before dialect-scoped metadata.
            protectedData = legacyValue;
            spanDialect ??= AnthropicModelSession.DialectName;
        }

        var isRedacted = metadata.TryGetValue(ReasoningMetadata.IsRedactedKey, out var rawRedacted) && rawRedacted is true;
        var itemId = metadata.TryGetValue(ReasoningMetadata.ItemIdKey, out var rawItemId) ? rawItemId as string : null;
        var geminiStep = metadata.TryGetValue(ReasoningMetadata.GeminiStepKey, out var rawGeminiStep) ? rawGeminiStep : null;

        if (targetDialect is null || string.Equals(spanDialect, targetDialect, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(protectedData) && !isRedacted && geminiStep is null)
            {
                return null;
            }

            MEAI.AdditionalPropertiesDictionary? additional = null;
            if (!string.IsNullOrEmpty(spanDialect))
            {
                (additional ??= [])[ReasoningMetadata.DialectKey] = spanDialect;
            }

            if (!string.IsNullOrEmpty(itemId))
            {
                (additional ??= [])["reasoningItemId"] = itemId;
            }

            if (isRedacted)
            {
                (additional ??= [])[ReasoningMetadata.IsRedactedKey] = true;
            }

            if (geminiStep is not null)
            {
                (additional ??= [])[ReasoningMetadata.GeminiStepKey] = geminiStep;
            }

            return new MEAI.TextReasoningContent(text)
            {
                ProtectedData = protectedData,
                AdditionalProperties = additional
            };
        }

        // Plain-text reasoning dialects can receive readable summaries without foreign cryptographic tokens.
        if (targetDialect is OpenAIModelSession.DialectName or "mistral" or OllamaModelSession.DialectName &&
            !isRedacted &&
            !string.IsNullOrEmpty(text))
        {
            return new MEAI.TextReasoningContent(text);
        }

        return null;
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
                .AsValueEnumerable()
                .Skip(startIndex)
                .Where(static message => message is not (ContextCompressionChatMessage or RootChatMessage or ChatImageOffloadMessage))
                .ToArray();
        }

        var suffixStartIndex = ResolveStartIndex(chatMessages, maxContextRounds, compressionIndex + 1);
        List<ChatMessage> result =
        [
            with(chatMessages.Count - suffixStartIndex + 1),
            chatMessages[compressionIndex],
            .. chatMessages
                .Skip(suffixStartIndex)
                .Where(static message => message is not (ContextCompressionChatMessage or RootChatMessage or ChatImageOffloadMessage)),
        ];
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
                .Where(static message => message is not (ContextCompressionChatMessage or RootChatMessage or ChatImageOffloadMessage))
                .ToArray();
            var startIndex = ResolveStartIndex(messages, maxContextRounds, 0);
            wasHistoryTrimmed = startIndex > 0;
            return messages.AsValueEnumerable().Skip(startIndex).ToArray();
        }

        var suffix = chatNodes
            .AsValueEnumerable()
            .Skip(anchorIndex + 1)
            .Select(static node => node.Message)
            .Where(static message => message is not (ContextCompressionChatMessage or RootChatMessage or ChatImageOffloadMessage))
            .ToArray();
        var suffixStartIndex = ResolveStartIndex(suffix, maxContextRounds, 0);
        wasHistoryTrimmed = suffixStartIndex > 0;
        var retainedUserMessages = SelectRetainedUserMessages(chatNodes, anchorIndex, declaredContextLimit);
        List<ChatMessage> result =
        [
            with(retainedUserMessages.Count + suffix.Length - suffixStartIndex + 1),
            .. retainedUserMessages,
            chatNodes[compressionIndex].Message,
            .. suffix.Skip(suffixStartIndex),
        ];
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
            if (chatMessages[i].Role != MEAI.ChatRole.User)
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

    private static IEnumerable<MEAI.ChatMessage> CaptureChatMessageContents(
        IPromptRenderer promptRenderer,
        ChatMessage chatMessage,
        bool canSendImages,
        IReadOnlySet<ChatImageOccurrence>? offloadedImages,
        List<PendingMedia> pendingImages,
        string? targetDialect,
        bool supportsToolResultBlocks)
    {
        switch (chatMessage)
        {
            case AssistantChatMessage assistantChatMessage:
            {
                var items = new List<MEAI.AIContent>();
                var spans = assistantChatMessage.Items;
                var nodeId = assistantChatMessage.Owner?.Id ?? Guid.Empty;
                for (var spanIndex = 0; spanIndex < spans.Count; spanIndex++)
                {
                    var span = spans[spanIndex];
                    switch (span)
                    {
                        case AssistantChatMessageTextSpan { Content: { Length: > 0 } content }:
                        {
                            items.Add(new MEAI.TextContent(content));
                            break;
                        }
                        case AssistantChatMessageReasoningSpan reasoningSpan:
                        {
                            if (CreateReasoningContent(reasoningSpan.ReasoningOutput, reasoningSpan.Metadata, targetDialect) is { } reasoning)
                            {
                                items.Add(reasoning);
                            }

                            break;
                        }
                        case AssistantChatMessageImageSpan { ImageOutput: { } imageOutput }:
                        {
                            AddImage(items, imageOutput, new ChatImageOccurrence(nodeId, spanIndex, 0));
                            break;
                        }
                        case AssistantChatMessageVisualContextResetSpan resetSpan:
                        {
                            if (items.Count > 0)
                            {
                                yield return new MEAI.ChatMessage(MEAI.ChatRole.Assistant, items);
                                items = [];
                            }

                            yield return new MEAI.ChatMessage(resetSpan.Message.Role, resetSpan.Message.Content);
                            break;
                        }
                        case AssistantChatMessageFunctionCallSpan { Items: { Count: > 0 } functionCalls }:
                        {
                            var spanDialect = span.Metadata.TryGetValue(ReasoningMetadata.DialectKey, out var rawSpanDialect) ?
                                rawSpanDialect as string :
                                null;
                            foreach (var call in functionCalls.SelectMany(static function => function.Calls))
                            {
                                items.Add(CaptureFunctionCall(call, spanDialect));
                            }

                            yield return new MEAI.ChatMessage(MEAI.ChatRole.Assistant, items);
                            items = [];

                            var resultItems = new List<MEAI.AIContent>();
                            var imageItems = new List<MEAI.AIContent>();
                            var imageIndex = 0;
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
                                    var modelResult = persistedResult is ChatFunctionResult structuredResult ?
                                        structuredResult.Value :
                                        persistedResult;
                                    if (resultContent is not null)
                                    {
                                        // Render prompt nodes now; neither the result container nor a mutable
                                        // attachment may be read again after asynchronous file loading begins.
                                        var capturedResult = modelResult switch
                                        {
                                            ToolContentResult rich => ProjectToolContent(
                                                rich,
                                                resultContent,
                                                imageItems,
                                                nodeId,
                                                spanIndex,
                                                ref imageIndex),
                                            PromptNode promptNode => promptNode.ToString(),
                                            FileAttachment file => DescribeFile(file),
                                            _ => modelResult,
                                        };
                                        resultItems.Add(new MEAI.FunctionResultContent(callId, capturedResult));
                                    }
                                    else
                                    {
                                        resultItems.Add(
                                            new MEAI.FunctionResultContent(
                                                callId,
                                                $"Error: No result found for function call ID '{callId}'. " +
                                                "This may caused by an error during function execution or user cancellation."));
                                    }

                                    switch (persistedResult)
                                    {
                                        case ViewImageFunctionResult imageResult:
                                        {
                                            foreach (var view in imageResult.Items.OfType<ViewImageItem.Success>())
                                            {
                                                AddImage(
                                                    imageItems,
                                                    view.Preview,
                                                    new ChatImageOccurrence(nodeId, spanIndex, imageIndex++),
                                                    callId: callId,
                                                    view: view);
                                            }

                                            break;
                                        }
                                        case VisualAttachmentFunctionResult { Content: { IsImage: true } screenshot }:
                                        {
                                            AddImage(
                                                imageItems,
                                                screenshot,
                                                new ChatImageOccurrence(nodeId, spanIndex, imageIndex++),
                                                callId: callId);
                                            break;
                                        }
                                        case ChatAttachment attachment and not FileAttachment:
                                        {
                                            CaptureAttachment(attachment, imageItems);
                                            break;
                                        }
                                    }
                                }
                            }

                            yield return new MEAI.ChatMessage(MEAI.ChatRole.Tool, resultItems);

                            // Some connectors accept only text in tool results. Keep the tool
                            // cycle intact, then supply its image blocks in a user message.
                            if (imageItems.Count > 0)
                            {
                                imageItems.Insert(0, new MEAI.TextContent("<ExtraToolCallResultAttachments>"));
                                imageItems.Add(new MEAI.TextContent("</ExtraToolCallResultAttachments>"));
                                yield return new MEAI.ChatMessage(MEAI.ChatRole.User, imageItems);
                            }

                            break;
                        }
                    }
                }

                if (items.Count > 0)
                {
                    yield return new MEAI.ChatMessage(MEAI.ChatRole.Assistant, items);
                }

                break;
            }
            case UserChatMessage userChatMessage:
            {
                var items = new List<MEAI.AIContent>();
                foreach (var chatAttachment in userChatMessage.Attachments.AsValueEnumerable().ToArray())
                {
                    CaptureAttachment(chatAttachment, items);
                }

                if (userChatMessage is UserStrategyChatMessage { Strategy.Body: { Length: > 0 } strategyBody } userStrategyMessage)
                {
                    var renderedContent = promptRenderer.RenderStrategyUserPrompt(
                        strategyBody,
                        userChatMessage.Content,
                        userStrategyMessage.PreprocessorResult);
                    items.Add(new MEAI.TextContent(renderedContent));
                }
                else if (userChatMessage.Content.Length > 0)
                {
                    items.Add(new MEAI.TextContent(userChatMessage.Content));
                }

                yield return new MEAI.ChatMessage(MEAI.ChatRole.User, items);
                break;
            }
            case ContextCompressionChatMessage { HasSummary: true } compression:
            {
                yield return new MEAI.ChatMessage(MEAI.ChatRole.User, compression.ToString());
                break;
            }
            case { Role.Value: "system" or "user" or "developer" or "tool" } when chatMessage.ToString() is { Length: > 0 } content:
            {
                yield return new MEAI.ChatMessage(chatMessage.Role, content);
                break;
            }
        }

        object ProjectToolContent(
            ToolContentResult rich,
            ToolResultRecord record,
            List<MEAI.AIContent> imageItems,
            Guid nodeId,
            int spanIndex,
            ref int imageIndex)
        {
            var parts = new ModelToolContent(rich.Blocks.Length, rich.IsError is true);
            foreach (var block in rich.Blocks)
            {
                switch (block)
                {
                    case ToolContentBlock.Text text:
                        parts.Add(new MEAI.TextContent(text.Value));
                        break;
                    case ToolContentBlock.Json json:
                        parts.Add(new MEAI.TextContent(json.Value.GetRawText()));
                        break;
                    case ToolContentBlock.Uri uri:
                        if (supportsToolResultBlocks &&
                            canSendImages &&
                            uri.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true &&
                            Uri.TryCreate(uri.Address, UriKind.Absolute, out var address) &&
                            address.Scheme == Uri.UriSchemeHttps)
                        {
                            var occurrence = new ChatImageOccurrence(nodeId, spanIndex, imageIndex++);
                            if (offloadedImages?.Contains(occurrence) != true)
                            {
                                parts.Add(new MEAI.UriContent(address, uri.MediaType));
                                pendingImages.Add(new RemoteImage(occurrence));
                                break;
                            }
                        }

                        parts.Add(new MEAI.TextContent(uri.Address));
                        break;
                    case ToolContentBlock.File file when (uint)file.Index < (uint)record.Files.Length:
                        var attachment = record.Files[file.Index].Attachment;
                        if (!attachment.IsImage)
                        {
                            parts.Add(new MEAI.TextContent($"Tool attachment: {attachment.MimeType}"));
                            break;
                        }

                        var position = new ChatImageOccurrence(nodeId, spanIndex, imageIndex++);
                        if (string.IsNullOrEmpty(attachment.FilePath))
                        {
                            parts.Add(new MEAI.TextContent("Tool image unavailable on this device."));
                            break;
                        }

                        if (!supportsToolResultBlocks)
                        {
                            parts.Add(new MEAI.TextContent("See the accompanying tool image."));
                            AddImage(imageItems, attachment, position, record.CallId);
                            break;
                        }

                        var index = parts.Count;
                        parts.Add(new MEAI.TextContent("Tool image omitted."));
                        if (canSendImages && offloadedImages?.Contains(position) != true)
                        {
                            var image = new ImageReference(attachment.FilePath, attachment.MimeType, true, null, null, record.CallId, null);
                            pendingImages.Add(
                                new PendingImage(
                                    image,
                                    position,
                                    null,
                                    null,
                                    (_, bytes, mime) => parts[index] = new MEAI.DataContent(bytes, mime),
                                    _ => parts[index] = new MEAI.TextContent("Tool image unavailable.")));
                        }

                        break;
                }
            }

            if (rich.StructuredContent is { } structured) parts.Add(new MEAI.TextContent(structured.GetRawText()));
            if (supportsToolResultBlocks && (parts.Count > 0 || parts.IsError)) return parts;
            if (rich.IsError is true) parts.Add(new MEAI.TextContent("The tool reported an error."));
            return string.Join("\n", parts.OfType<MEAI.TextContent>().Select(static part => part.Text));
        }

        void AddImage(
            List<MEAI.AIContent> target,
            FileAttachment file,
            ChatImageOccurrence occurrence,
            string? callId = null,
            ViewImageItem.Success? view = null)
        {
            var image = new ImageReference(
                file.FilePath,
                file.MimeType,
                view is not null,
                view?.Width,
                view?.Height,
                callId,
                view?.SourcePath);
            var originalWidth = view?.OriginalWidth;
            var originalHeight = view?.OriginalHeight;
            if (!canSendImages)
            {
                target.Add(
                    new MEAI.TextContent(
                        DescribeImage(
                            image,
                            originalWidth,
                            originalHeight,
                            "Image not provided: this model does not accept image input.")));
                return;
            }

            if (offloadedImages?.Contains(occurrence) == true)
            {
                target.Add(
                    new MEAI.TextContent(
                        DescribeImage(
                            image,
                            originalWidth,
                            originalHeight,
                            "Image omitted to fit the request. If needed, use view_image on the path, with fewer images or a smaller crop.")));
                return;
            }

            var placeholder = new MEAI.TextContent(string.Empty);
            target.Add(placeholder);
            pendingImages.Add(new PendingImage(image, occurrence, originalWidth, originalHeight, Provide, Omit));

            void Provide(string description, byte[] bytes, string mimeType)
            {
                var index = target.IndexOf(placeholder);
                target[index] = new MEAI.TextContent(description);
                target.Insert(index + 1, new MEAI.DataContent(bytes, mimeType));
            }

            void Omit(string description)
            {
                target[target.IndexOf(placeholder)] = new MEAI.TextContent(description);
            }
        }
    }

    private static MEAI.FunctionCallContent CaptureFunctionCall(ToolCallRecord call, string? spanDialect)
    {
        var callId = call.Id ?? string.Empty;
        var arguments = call.Arguments is null ? null : new Dictionary<string, object?>(call.Arguments);
        var callDialect = call.Metadata is not null && call.Metadata.TryGetValue(ReasoningMetadata.DialectKey, out var rawDialect) ?
            rawDialect as string :
            spanDialect;

        MEAI.AdditionalPropertiesDictionary? additional = null;
        if (!string.IsNullOrEmpty(callDialect))
        {
            additional = new MEAI.AdditionalPropertiesDictionary
            {
                [ReasoningMetadata.DialectKey] = callDialect
            };
        }

        return new MEAI.FunctionCallContent(callId, call.FunctionName, arguments)
        {
            AdditionalProperties = additional
        };
    }

    private static void CaptureAttachment(ChatAttachment chatAttachment, List<MEAI.AIContent> contents)
    {
        switch (chatAttachment)
        {
            case TextSelectionAttachment textSelection:
            {
                var incompleteAttribute = textSelection.IsTextIncomplete ? " incomplete=\"true\"" : string.Empty;
                contents.Add(
                    new MEAI.TextContent(
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
                    new MEAI.TextContent(
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
                    new MEAI.TextContent(
                        $"""
                         <Attachment type="text">
                         {text}
                         </Attachment>
                         """));
                break;
            }
            case FileAttachment file:
            {
                contents.Add(new MEAI.TextContent(DescribeFile(file)));
                break;
            }
        }
    }

    private static string DescribeFile(FileAttachment file)
    {
        return $"""
                <Attachment type="file" name="{SecurityElement.Escape(Path.GetFileName(file.FilePath))}" path="{SecurityElement.Escape(file.FilePath)}" mimeType="{SecurityElement.Escape(file.MimeType)}" description="{SecurityElement.Escape(file.Description)}">
                File reference only. Use the relevant tool to inspect it if needed.
                </Attachment>
                """;
    }

    private static string DescribeImage(ImageReference image, int? originalWidth, int? originalHeight, string status)
    {
        var dimensions = image is { Width: { } width, Height: { } height } ? $" pixels=\"{width}x{height}\"" : string.Empty;
        var original = originalWidth is { } ow && originalHeight is { } oh ? $" originalPixels=\"{ow}x{oh}\"" : string.Empty;
        var source = image.SourcePath is { } path ? $" sourcePath=\"{SecurityElement.Escape(path)}\"" : string.Empty;
        var call = image.CallId is { } id ? $" callId=\"{SecurityElement.Escape(id)}\"" : string.Empty;
        return $"""
                <Image path="{SecurityElement.Escape(image.Path)}" mimeType="{SecurityElement.Escape(image.MimeType)}"{source}{call}{dimensions}{original}>
                {SecurityElement.Escape(status)}
                </Image>
                """;
    }

    private sealed record ImageReference(
        string Path,
        string MimeType,
        bool IsSnapshot,
        int? Width,
        int? Height,
        string? CallId,
        string? SourcePath
    );

    private abstract record PendingMedia(ChatImageOccurrence Occurrence);

    private sealed record RemoteImage(ChatImageOccurrence Occurrence) : PendingMedia(Occurrence);

    // The placeholder connects the captured position with its disk read without revisiting live messages.
    private sealed record PendingImage(
        ImageReference Image,
        ChatImageOccurrence Occurrence,
        int? OriginalWidth,
        int? OriginalHeight,
        Action<string, byte[], string> Provide,
        Action<string> Omit
    ) : PendingMedia(Occurrence);
}

public sealed record ChatModelInput(IList<MEAI.ChatMessage> History, IReadOnlyList<ChatImageOccurrence> SentImages);