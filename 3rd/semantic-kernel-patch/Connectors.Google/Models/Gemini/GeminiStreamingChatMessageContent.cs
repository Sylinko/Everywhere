// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Text;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Microsoft.SemanticKernel.Connectors.Google;

/// <summary>
/// Gemini specialized streaming chat message content
/// </summary>
public sealed class GeminiStreamingChatMessageContent : StreamingChatMessageContent
{
    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiStreamingChatMessageContent"/> class.
    /// </summary>
    /// <param name="role">Role of the author of the message</param>
    /// <param name="content">Content of the message</param>
    /// <param name="modelId">The model ID used to generate the content</param>
    /// <param name="choiceIndex">Choice index</param>
    /// <param name="calledToolResult">The result of tool called by the kernel.</param>
    /// <param name="metadata">Additional metadata</param>
    /// <param name="innerContent">The raw provider representation.</param>
    internal GeminiStreamingChatMessageContent(
        AuthorRole? role,
        string? content,
        string modelId,
        int choiceIndex,
        GeminiFunctionToolResult? calledToolResult = null,
        GeminiMetadata? metadata = null,
        object? innerContent = null)
        : base(
            role: role,
            content: content,
            innerContent: innerContent ?? content,
            choiceIndex: choiceIndex,
            modelId: modelId,
            encoding: Encoding.UTF8,
            metadata: metadata)
    {
        this.CalledToolResult = calledToolResult;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="GeminiStreamingChatMessageContent"/> class.
    /// </summary>
    /// <param name="role">Role of the author of the message</param>
    /// <param name="content">Content of the message</param>
    /// <param name="modelId">The model ID used to generate the content</param>
    /// <param name="choiceIndex">Choice index</param>
    /// <param name="toolCalls">Tool calls returned by model</param>
    /// <param name="metadata">Additional metadata</param>
    /// <param name="globalChoiceIndex">The index of function call</param>
    /// <param name="innerContent">The raw provider representation.</param>
    internal GeminiStreamingChatMessageContent(
        AuthorRole role,
        string? content,
        string modelId,
        int choiceIndex,
        IReadOnlyList<GeminiFunctionToolCall>? toolCalls,
        ref int globalChoiceIndex,
        GeminiMetadata? metadata = null,
        object? innerContent = null)
        : base(
            role: role,
            content: content,
            modelId: modelId,
            innerContent: innerContent ?? content,
            choiceIndex: choiceIndex,
            encoding: Encoding.UTF8,
            metadata: metadata)
    {
        this.ToolCalls = toolCalls;

        // Add StreamingFunctionCallUpdateContent items for each tool call for standard SK processing
        if (this.ToolCalls is { Count: > 0 })
        {
            for (int i = 0; i < this.ToolCalls.Count; i++)
            {
                var toolCall = this.ToolCalls[i];
                var arguments = toolCall.Arguments is not null
                    ? System.Text.Json.JsonSerializer.Serialize(toolCall.Arguments)
                    : null;

                // Create metadata dictionary with thoughtSignature if present
                IReadOnlyDictionary<string, object?>? functionCallMetadata = null;
                if (toolCall.ThoughtSignature is not null)
                {
                    functionCallMetadata = new Dictionary<string, object?>
                    {
                        ["thoughtSignature"] = toolCall.ThoughtSignature
                    };
                }

                this.Items.Add(new StreamingFunctionCallUpdateContent(
                    callId: toolCall.FullyQualifiedName,
                    name: toolCall.FullyQualifiedName,
                    arguments: arguments,
                    functionCallIndex: i)
                {
                    RequestIndex = globalChoiceIndex++,
                    Metadata = functionCallMetadata
                });
            }
        }
    }

    /// <summary>
    /// A list of the tools returned by the model with arguments.
    /// </summary>
    public IReadOnlyList<GeminiFunctionToolCall>? ToolCalls { get; }

    /// <summary>
    /// The result of tool called by the kernel.
    /// </summary>
    public GeminiFunctionToolResult? CalledToolResult { get; }

    /// <summary>
    /// The metadata associated with the content.
    /// </summary>
    public new GeminiMetadata? Metadata => (GeminiMetadata?)base.Metadata;
}
