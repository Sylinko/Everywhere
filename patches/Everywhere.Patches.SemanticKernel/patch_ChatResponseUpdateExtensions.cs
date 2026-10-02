// ReSharper disable UnusedType.Global
// ReSharper disable UnusedMember.Global

using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using MonoMod;
using FunctionCallContent = Microsoft.Extensions.AI.FunctionCallContent;
using TextContent = Microsoft.Extensions.AI.TextContent;

namespace Everywhere.Patches.SemanticKernel;

/// <summary>
/// Preserves assistant content and completion evidence when converting MEAI streaming
/// updates into the Semantic Kernel content types used by Everywhere.
/// </summary>
/// <remarks>
/// The upstream text/tool conversion is retained and extended to carry reasoning,
/// protected payloads, and per-item metadata. Explicit finish reasons are stored in
/// message metadata because SK has no corresponding property, alongside reported
/// usage. No finish reason is inferred from stream termination, and provider-owned
/// metadata dictionaries are not mutated.
/// See <see href="https://github.com/microsoft/semantic-kernel/blob/dc7c1c048488a8b611b5382d0d7efe05608a9fa0/dotnet/src/SemanticKernel.Abstractions/AI/ChatClient/ChatResponseUpdateExtensions.cs">the pinned upstream converter</see>.
/// </remarks>
[MonoModPatch("Microsoft.Extensions.AI.ChatResponseUpdateExtensions")]
internal static class patch_ChatResponseUpdateExtensions
{
    // Based on the complete SK 1.75.0 conversion. Everywhere extensions are marked below.
    [MonoModReplace]
    internal static StreamingChatMessageContent ToStreamingChatMessageContent(this ChatResponseUpdate update)
    {
        var content = new StreamingChatMessageContent(
            update.Role is not null ? new AuthorRole(update.Role.Value.Value) : null,
            null)
        {
            InnerContent = update.RawRepresentation,
            Metadata = update.AdditionalProperties,
            ModelId = update.ModelId
        };

        // Everywhere: SK has no FinishReason property; retain explicit MEAI evidence in
        // metadata without mutating the provider's AdditionalProperties dictionary.
        if (update.FinishReason is { } finishReason)
            content.Metadata = new Dictionary<string, object?>(content.Metadata ?? new Dictionary<string, object?>()) { [nameof(update.FinishReason)] = finishReason.Value };

        foreach (var item in update.Contents)
        {
            StreamingKernelContent resultContent;
            Dictionary<string, object?>? metadata = null;
            switch (item)
            {
                case TextContent textContent:
                {
                    resultContent = new StreamingTextContent(textContent.Text);
                    break;
                }
                case FunctionCallContent functionCallContent:
                {
                    resultContent = new StreamingFunctionCallUpdateContent(
                        functionCallContent.CallId,
                        functionCallContent.Name,
                        functionCallContent.Arguments is not null ?
                            JsonSerializer.Serialize(functionCallContent.Arguments, AbstractionsJsonContext.Default.IDictionaryStringObject) :
                            null);
                    break;
                }
                case TextReasoningContent textReasoningContent:
                {
                    // Everywhere: preserve reasoning and its encrypted/protected payload.
                    resultContent = new StreamingReasoningContent(textReasoningContent.Text);
                    if (textReasoningContent.ProtectedData is { Length: > 0 })
                    {
                        metadata = new Dictionary<string, object?>(1)
                        {
                            { "ProtectedData", textReasoningContent.ProtectedData }
                        };
                    }
                    break;
                }
                case UsageContent usageContent:
                {
                    // Everywhere: preserve previously captured finish evidence alongside usage.
                    content.Metadata = new Dictionary<string, object?>(content.Metadata ?? new Dictionary<string, object?>())
                    {
                        ["Usage"] = usageContent
                    };
                    continue;
                }
                default:
                {
                    continue;
                }
            }

            // Everywhere: carry per-item metadata through the conversion.
            resultContent.Metadata = Union(metadata, item.AdditionalProperties);
            resultContent.InnerContent = item.RawRepresentation;
            resultContent.ModelId = update.ModelId;
            content.Items.Add(resultContent);
        }

        return content;
    }

    private static IReadOnlyDictionary<string,object?>? Union(Dictionary<string, object?>? metadata1, AdditionalPropertiesDictionary? metadata2)
    {
        if (metadata1 is null) return metadata2;
        if (metadata2 is null) return metadata1;

        foreach (var kvp in metadata2) metadata1[kvp.Key] = kvp.Value;
        return metadata1;
    }
}