// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license (LICENSE.txt).
// Complete streaming conversion copied from Microsoft.Extensions.AI.OpenAI 10.9.0,
// commit b10f9c0a081b5dbb7755b8f5592e1d3c3f550a3a. Everywhere changes are marked below.
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using MonoMod;
using OpenAI.Responses;

namespace Everywhere.Patches.MicrosoftExtensionsAI.OpenAI;

/// <summary>
/// Preserves terminal Responses evidence that MEAI's streaming conversion currently
/// treats like a progress event: incomplete reasons/usage and failed-response errors.
/// </summary>
/// <remarks>
/// The complete upstream conversion is retained, including tool content, reasoning,
/// annotations, and continuation state. Terminal cases reuse the SDK's own reason and
/// usage mappings and its existing ErrorContent contract. This makes the evidence
/// available to Everywhere without interpreting it as an application retry decision.
/// See <see href="https://github.com/dotnet/extensions/blob/b10f9c0a081b5dbb7755b8f5592e1d3c3f550a3a/src/Libraries/Microsoft.Extensions.AI.OpenAI/OpenAIResponsesChatClient.cs">the pinned upstream converter</see>.
/// </remarks>
[MonoModPatch("Microsoft.Extensions.AI.OpenAIResponsesChatClient")]
internal static class patch_OpenAIResponsesChatClient
{
    [MonoModReplace]
    internal static async IAsyncEnumerable<ChatResponseUpdate> FromOpenAIStreamingResponseUpdatesAsync(
        IAsyncEnumerable<StreamingResponseUpdate> streamingResponseUpdates,
        CreateResponseOptions? options,
        string? conversationId,
        string? resumeResponseId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var createdAt = (DateTimeOffset?)null;
        var responseId = resumeResponseId;
        var modelId = (string?)null;
        var lastMessageId = (string?)null;
        var lastRole = (ChatRole?)null;
        var hasAnyFunctions = false;
        var isStoredOutputDisabled = false;
        var serviceTier = (string?)null;
        var systemFingerprint = (string?)null;
        var latestResponseStatus = (ResponseStatus?)null;
        var mcpApprovalRequests = (Dictionary<string, ToolApprovalRequestContent>?)null;

        UpdateConversationId(resumeResponseId);

        await foreach (var streamingUpdate in streamingResponseUpdates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            // Create an update populated with the current state of the response.
            ChatResponseUpdate CreateUpdate(AIContent? content = null) =>
                new(lastRole, content is not null ? [content] : null)
                {
                    ContinuationToken = CreateContinuationToken(
                        responseId!,
                        latestResponseStatus,
                        options?.BackgroundModeEnabled,
                        streamingUpdate.SequenceNumber),
                    ConversationId = conversationId,
                    CreatedAt = createdAt,
                    MessageId = lastMessageId,
                    ModelId = modelId,
                    RawRepresentation = streamingUpdate,
                    ResponseId = responseId,
                };

            switch (streamingUpdate)
            {
                case StreamingResponseCreatedUpdate createdUpdate:
                    createdAt = createdUpdate.Response.CreatedAt;
                    responseId = createdUpdate.Response.Id;
                    UpdateConversationId(responseId, createdUpdate.Response);
                    modelId = createdUpdate.Response.Model;
                    latestResponseStatus = createdUpdate.Response.Status;
                    goto default;

                case StreamingResponseQueuedUpdate queuedUpdate:
                    createdAt = queuedUpdate.Response.CreatedAt;
                    responseId = queuedUpdate.Response.Id;
                    UpdateConversationId(responseId, queuedUpdate.Response);
                    modelId = queuedUpdate.Response.Model;
                    latestResponseStatus = queuedUpdate.Response.Status;
                    goto default;

                case StreamingResponseInProgressUpdate inProgressUpdate:
                    createdAt = inProgressUpdate.Response.CreatedAt;
                    responseId = inProgressUpdate.Response.Id;
                    UpdateConversationId(responseId, inProgressUpdate.Response);
                    modelId = inProgressUpdate.Response.Model;
                    latestResponseStatus = inProgressUpdate.Response.Status;
                    goto default;

                case StreamingResponseIncompleteUpdate incompleteUpdate:
                {
                    createdAt = incompleteUpdate.Response.CreatedAt;
                    responseId = incompleteUpdate.Response.Id;
                    UpdateConversationId(responseId, incompleteUpdate.Response);
                    modelId = incompleteUpdate.Response.Model;
                    latestResponseStatus = incompleteUpdate.Response.Status;
                    // Everywhere: incomplete is terminal evidence, not an ordinary progress event.
                    // Reuse the SDK's existing reason/usage conversions without inventing completion.
                    var update = CreateUpdate(ToUsageDetails(incompleteUpdate.Response) is { } usage ? new UsageContent(usage) : null);
                    update.FinishReason = AsFinishReason(incompleteUpdate.Response.IncompleteStatusDetails?.Reason);
                    yield return update;
                    break;
                }

                case StreamingResponseFailedUpdate failedUpdate:
                {
                    createdAt = failedUpdate.Response.CreatedAt;
                    responseId = failedUpdate.Response.Id;
                    UpdateConversationId(responseId, failedUpdate.Response);
                    modelId = failedUpdate.Response.Model;
                    latestResponseStatus = failedUpdate.Response.Status;
                    // Everywhere: retain the terminal error in the same content type used by
                    // the SDK's error events. The application's existing wrapper handles it.
                    var error = failedUpdate.Response.Error;
                    yield return CreateUpdate(error is null ? null : new ErrorContent(error.Message) { ErrorCode = error.Code.ToString() });
                    break;
                }

                case StreamingResponseCompletedUpdate completedUpdate:
                {
                    createdAt = completedUpdate.Response.CreatedAt;
                    responseId = completedUpdate.Response.Id;
                    UpdateConversationId(responseId, completedUpdate.Response);
                    modelId = completedUpdate.Response.Model;
                    latestResponseStatus = completedUpdate.Response?.Status;
                    var update = CreateUpdate(ToUsageDetails(completedUpdate.Response) is { } usage ? new UsageContent(usage) : null);
                    update.FinishReason =
                        AsFinishReason(completedUpdate.Response?.IncompleteStatusDetails?.Reason) ??
                        (hasAnyFunctions ? ChatFinishReason.ToolCalls :
                        ChatFinishReason.Stop);
                    yield return update;
                    break;
                }

                case StreamingResponseOutputItemAddedUpdate outputItemAddedUpdate:
                    switch (outputItemAddedUpdate.Item)
                    {
                        case MessageResponseItem mri:
                            lastMessageId = outputItemAddedUpdate.Item.Id;
                            lastRole = AsChatRole(mri.Role);
                            break;

                        case FunctionCallResponseItem:
                            hasAnyFunctions = true;
                            lastRole = ChatRole.Assistant;
                            break;
                    }

                    goto default;

                case StreamingResponseOutputTextDeltaUpdate outputTextDeltaUpdate:
                    yield return CreateUpdate(new TextContent(outputTextDeltaUpdate.Delta));
                    break;

                case StreamingResponseReasoningSummaryTextDeltaUpdate reasoningSummaryTextDeltaUpdate:
                    yield return CreateUpdate(CreateReasoningContent(reasoningSummaryTextDeltaUpdate.Delta, protectedData: null, reasoningSummaryTextDeltaUpdate.ItemId));
                    break;

                case StreamingResponseReasoningTextDeltaUpdate reasoningTextDeltaUpdate:
                    yield return CreateUpdate(CreateReasoningContent(reasoningTextDeltaUpdate.Delta, protectedData: null, reasoningTextDeltaUpdate.ItemId));
                    break;

                case StreamingResponseImageGenerationCallInProgressUpdate imageGenInProgress:
                    yield return CreateUpdate(new ImageGenerationToolCallContent(imageGenInProgress.ItemId)
                    {
                        RawRepresentation = imageGenInProgress,
                    });
                    break;

                case StreamingResponseImageGenerationCallPartialImageUpdate streamingImageGenUpdate:
                    yield return CreateUpdate(GetImageGenerationResult(streamingImageGenUpdate, options));
                    break;

                case StreamingResponseCodeInterpreterCallCodeDeltaUpdate codeInterpreterDeltaUpdate:
                    yield return CreateUpdate(new CodeInterpreterToolCallContent(codeInterpreterDeltaUpdate.ItemId)
                    {
                        Inputs = [new DataContent(Encoding.UTF8.GetBytes(codeInterpreterDeltaUpdate.Delta), OpenAIClientExtensions.PythonMediaType)],
                        RawRepresentation = codeInterpreterDeltaUpdate,
                    });
                    break;

                case StreamingResponseWebSearchCallInProgressUpdate webSearchInProgressUpdate:
                    yield return CreateUpdate(new WebSearchToolCallContent(webSearchInProgressUpdate.ItemId)
                    {
                        RawRepresentation = webSearchInProgressUpdate,
                    });
                    break;

                case StreamingResponseOutputItemDoneUpdate outputItemDoneUpdate:
                    switch (outputItemDoneUpdate.Item)
                    {
                        // Translate completed ResponseItems into their corresponding abstraction representations.
                        case FunctionCallResponseItem fcri:
                            yield return CreateUpdate(OpenAIClientExtensions.ParseCallContent(fcri.FunctionArguments.ToString(), fcri.CallId, fcri.FunctionName));
                            break;

                        case McpToolCallItem mtci:
                            var mcpUpdate = CreateUpdate();
                            AddMcpToolCallContent(mtci, mcpUpdate.Contents);
                            yield return mcpUpdate;
                            break;

                        case McpToolCallApprovalRequestItem mtcari:
                            // We are reusing the mtcari.Id as the McpServerToolCallContent.CallId since we don't have one yet.
                            var streamApprovalRequest = new ToolApprovalRequestContent(mtcari.Id, new McpServerToolCallContent(mtcari.Id, mtcari.ToolName, mtcari.ServerLabel)
                            {
                                Arguments = JsonSerializer.Deserialize(mtcari.ToolArguments, OpenAIJsonContext.Default.IDictionaryStringObject),
                                RawRepresentation = mtcari,
                            })
                            {
                                RawRepresentation = mtcari,
                            };

                            // Store for correlation with responses.
                            (mcpApprovalRequests ??= new())[mtcari.Id] = streamApprovalRequest;
                            yield return CreateUpdate(streamApprovalRequest);
                            break;

                        case McpToolCallApprovalResponseItem mtcari
                            when mcpApprovalRequests?.TryGetValue(mtcari.ApprovalRequestId, out var request) is true:
                            _ = mcpApprovalRequests.Remove(mtcari.ApprovalRequestId);

                            // Correlate with the original request to reuse its ToolCall.
                            // McpToolCallApprovalResponseItem without a correlated request falls through to default.
                            yield return CreateUpdate(new ToolApprovalResponseContent(
                                mtcari.ApprovalRequestId,
                                mtcari.Approved,
                                request.ToolCall)
                            {
                                RawRepresentation = mtcari,
                            });
                            break;

                        case FunctionCallOutputResponseItem functionCallOutputItem:
                            lastRole ??= ChatRole.Assistant;
                            yield return CreateUpdate(new FunctionResultContent(functionCallOutputItem.CallId, functionCallOutputItem.FunctionOutput) { RawRepresentation = functionCallOutputItem });
                            break;

                        case CodeInterpreterCallResponseItem cicri:
                            // The CodeInterpreterToolCallContent has already been yielded as part of delta updates.
                            // Only yield the CodeInterpreterToolResultContent here for the outputs.
                            yield return CreateUpdate(CreateCodeInterpreterResultContent(cicri));
                            break;

                        case WebSearchCallResponseItem wscri:
                            // The WebSearchToolCallContent has already been yielded as part of in-progress updates.
                            // Yield a second one here with queries populated, which coalescing will merge with the first.
                            yield return CreateUpdate(new WebSearchToolCallContent(wscri.Id)
                            {
                                Queries = GetWebSearchQueries(wscri),
                            });

                            // Also yield the WebSearchToolResultContent.
                            yield return CreateUpdate(new WebSearchToolResultContent(wscri.Id)
                            {
                                Outputs = GetWebSearchSources(wscri),
                                RawRepresentation = wscri,
                            });
                            break;

                        // MessageResponseItems will have already had their content yielded as part of delta updates.
                        // However, those deltas didn't yield annotations. If there are any annotations, yield them now.
                        case MessageResponseItem { Content: { Count: > 0 } mriContent } when mriContent.Any(c => c.OutputTextAnnotations is { Count: > 0 }):
                            var annotatedContent = new AIContent(); // do not include RawRepresentation to avoid duplication with already yielded deltas
                            foreach (var c in mriContent)
                            {
                                PopulateAnnotations(c, annotatedContent);
                            }
                            yield return CreateUpdate(annotatedContent);
                            break;

                        // For ReasoningResponseItem, if there's encrypted content, we need to yield that
                        // so that it can be coalesced with the streamed text deltas and roundtripped.
                        // Since we may have already yielded reasoning deltas, we explicitly avoid setting
                        // the RawRepresentation here to avoid duplication, as when roundtripping that
                        // raw representation will be preferred. The reasoning item's id is stashed in
                        // AdditionalProperties (which survives coalescing and serialization, unlike
                        // RawRepresentation) so that it can be sent back on a subsequent request; stateless
                        // (store=false) resume of encrypted reasoning is rejected by the service without it.
                        case ReasoningResponseItem { EncryptedContent: { Length: > 0 } encryptedContent } rri:
                            yield return CreateUpdate(CreateReasoningContent(text: null, protectedData: encryptedContent, itemId: rri.Id));
                            break;

                        // For ResponseItems where we've already yielded partial deltas for the whole content,
                        // we still want to yield an update, but we don't want it to include the ResponseItem
                        // as the RawRepresentation, since if it did, when roundtripping we'd end up sending
                        // the same content twice (first from the deltas, then from the raw response item).
                        // Just yield an update without AIContent for the ResponseItem.
                        case MessageResponseItem or ReasoningResponseItem or ImageGenerationCallResponseItem:
                            yield return CreateUpdate();
                            break;

                        // FileSearch items contain both the call and results inline, so we emit a call+result pair.
                        // ComputerCall results arrive as a separate ComputerCallOutputResponseItem.
                        case FileSearchCallResponseItem:
                            var toolCallUpdate = CreateUpdate(new ToolCallContent(outputItemDoneUpdate.Item.Id));
                            toolCallUpdate.Contents.Add(new ToolResultContent(outputItemDoneUpdate.Item.Id) { RawRepresentation = outputItemDoneUpdate.Item });
                            yield return toolCallUpdate;
                            break;

                        case ComputerCallResponseItem computerCall:
                            yield return CreateUpdate(new ToolCallContent(computerCall.CallId) { RawRepresentation = outputItemDoneUpdate.Item });
                            break;

                        case ComputerCallOutputResponseItem computerCallOutput:
                            yield return CreateUpdate(new ToolResultContent(computerCallOutput.CallId) { RawRepresentation = computerCallOutput });
                            break;

                        // For everything else, yield an AIContent for the ResponseItem.
                        default:
                            yield return CreateUpdate(new AIContent { RawRepresentation = outputItemDoneUpdate.Item });
                            break;
                    }
                    break;

                case StreamingResponseErrorUpdate errorUpdate:
                    var errorMessage = errorUpdate.Message;
                    var errorCode = errorUpdate.Code;
                    var errorParam = errorUpdate.Param;

                    // Workaround for https://github.com/openai/openai-dotnet/issues/849.
                    // The OpenAI service is sending down error information in a different format
                    // than is documented and thus a different format from what the OpenAI client
                    // library deserializes. Until that's addressed such that the data is correctly
                    // propagated through the OpenAI library, if it looks like the update doesn't
                    // contain the properly deserialized error information, try accessing it
                    // directly from the underlying JSON.
                    {
                        if (string.IsNullOrEmpty(errorMessage))
                        {
                            _ = errorUpdate.Patch.TryGetValue("$.error.message"u8, out errorMessage);
                        }

                        if (string.IsNullOrEmpty(errorCode))
                        {
                            _ = errorUpdate.Patch.TryGetValue("$.error.code"u8, out errorCode);
                        }

                        if (string.IsNullOrEmpty(errorParam))
                        {
                            _ = errorUpdate.Patch.TryGetValue("$.error.param"u8, out errorParam);
                        }
                    }

                    yield return CreateUpdate(new ErrorContent(errorMessage)
                    {
                        ErrorCode = errorCode,
                        Details = errorParam,
                    });
                    break;

                case StreamingResponseRefusalDoneUpdate refusalDone:
                    yield return CreateUpdate(new ErrorContent(refusalDone.Refusal)
                    {
                        ErrorCode = nameof(ResponseContentPart.Refusal),
                    });
                    break;

                default:
                    yield return CreateUpdate();
                    break;
            }
        }

        void UpdateConversationId(string? id, ResponseResult? response = null)
        {
            // Record the service tier and system fingerprint each once if not yet recorded.
            OpenAIClientExtensions.AddOpenAIResponseAttributes(
                response?.ServiceTier?.ToString(), systemFingerprint: null,
                ref serviceTier, ref systemFingerprint);

            isStoredOutputDisabled |= IsStoredOutputDisabled(options, response);
            if (isStoredOutputDisabled)
            {
                conversationId = null;
            }
            else
            {
                conversationId ??= id;
            }
        }
    }

    [MonoModIgnore]
    private static extern ResponsesClientContinuationToken? CreateContinuationToken(string responseId, ResponseStatus? responseStatus, bool? isBackgroundModeEnabled, int? updateSequenceNumber = null);

    [MonoModIgnore]
    private static extern bool IsStoredOutputDisabled(CreateResponseOptions? options, ResponseResult? response);

    [MonoModIgnore]
    private static extern ChatFinishReason? AsFinishReason(ResponseIncompleteStatusReason? statusReason);

    [MonoModIgnore]
    private static extern ChatRole AsChatRole(MessageRole? role);

    [MonoModIgnore]
    private static extern UsageDetails? ToUsageDetails(ResponseResult? responseResult);

    [MonoModIgnore]
    private static extern TextReasoningContent CreateReasoningContent(string? text, string? protectedData, string? itemId, object? rawRepresentation = null);

    [MonoModIgnore]
    private static extern ImageGenerationToolResultContent GetImageGenerationResult(StreamingResponseImageGenerationCallPartialImageUpdate update, CreateResponseOptions? options);

    [MonoModIgnore]
    private static extern void AddMcpToolCallContent(McpToolCallItem item, IList<AIContent> contents);

    [MonoModIgnore]
    private static extern CodeInterpreterToolResultContent CreateCodeInterpreterResultContent(CodeInterpreterCallResponseItem item);

    [MonoModIgnore]
    private static extern List<string>? GetWebSearchQueries(WebSearchCallResponseItem item);

    [MonoModIgnore]
    private static extern List<AIContent>? GetWebSearchSources(WebSearchCallResponseItem item);

    [MonoModIgnore]
    private static extern void PopulateAnnotations(ResponseContentPart source, AIContent destination);
}