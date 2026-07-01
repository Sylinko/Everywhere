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

[MonoModPatch("Microsoft.Extensions.AI.ChatResponseUpdateExtensions")]
internal static class patch_ChatResponseUpdateExtensions
{
    [MonoModReplace]
    internal static StreamingChatMessageContent ToStreamingChatMessageContent(this ChatResponseUpdate update)
    {
        StreamingChatMessageContent content = new(
            update.Role is not null ? new AuthorRole(update.Role.Value.Value) : null,
            null)
        {
            InnerContent = update.RawRepresentation,
            Metadata = update.AdditionalProperties,
            ModelId = update.ModelId
        };

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
                    content.Metadata = new Dictionary<string, object?>(update.AdditionalProperties ?? [])
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