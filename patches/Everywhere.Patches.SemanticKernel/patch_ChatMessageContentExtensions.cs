using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using MonoMod;
using FunctionCallContent = Microsoft.SemanticKernel.FunctionCallContent;
using FunctionResultContent = Microsoft.SemanticKernel.FunctionResultContent;
using TextContent = Microsoft.SemanticKernel.TextContent;

namespace Everywhere.Patches.SemanticKernel;

[MonoModPatch("Microsoft.SemanticKernel.ChatMessageContentExtensions")]
internal static class patch_ChatMessageContentExtensions
{
    // ReSharper disable once InconsistentNaming
    // ReSharper disable once RedundantAssignment
    [Experimental("SKEXP0001")]
    internal static ChatMessage ToChatMessage(this ChatMessageContent content)
    {
        ChatMessage message = new()
        {
            AdditionalProperties = content.Metadata is not null ? new AdditionalPropertiesDictionary(content.Metadata) : null,
            AuthorName = content.AuthorName,
            RawRepresentation = content.InnerContent,
            Role = content.Role.Label is { } label ? new ChatRole(label) : ChatRole.User,
        };

        foreach (var item in content.Items)
        {
            AIContent? aiContent = null;
            switch (item)
            {
                case TextContent textContent:
                {
                    aiContent = new Microsoft.Extensions.AI.TextContent(textContent.Text);
                    break;
                }
                case ReasoningContent reasoningContent:
                {
                    aiContent = new TextReasoningContent(reasoningContent.Text)
                    {
                        ProtectedData = reasoningContent.Metadata?.TryGetValue("ProtectedData", out var protectedData) is true ?
                            protectedData as string :
                            null
                    };
                    break;
                }
                case ImageContent imageContent:
                {
                    aiContent =
                        imageContent.DataUri is not null ? new DataContent(imageContent.DataUri, imageContent.MimeType) :
                        imageContent.Uri is not null ? new UriContent(imageContent.Uri, imageContent.MimeType ?? "image/*") :
                        null;
                    break;
                }
                case AudioContent audioContent:
                {
                    aiContent =
                        audioContent.DataUri is not null ? new DataContent(audioContent.DataUri, audioContent.MimeType) :
                        audioContent.Uri is not null ? new UriContent(audioContent.Uri, audioContent.MimeType ?? "audio/*") :
                        null;
                    break;
                }
                case BinaryContent binaryContent:
                {
                    aiContent =
                        binaryContent.DataUri is not null ? new DataContent(binaryContent.DataUri, binaryContent.MimeType) :
                        binaryContent.Uri is not null ? new UriContent(binaryContent.Uri, binaryContent.MimeType ?? "application/octet-stream") :
                        null;
                    break;
                }
                case FunctionCallContent functionCallContent:
                {
                    aiContent = new Microsoft.Extensions.AI.FunctionCallContent(
                        functionCallContent.Id ?? string.Empty,
                        functionCallContent.FunctionName,
                        functionCallContent.Arguments);
                    break;
                }
                case FunctionResultContent functionResultContent:
                {
                    aiContent = new Microsoft.Extensions.AI.FunctionResultContent(
                        functionResultContent.CallId ?? string.Empty,
                        functionResultContent.Result);
                    break;
                }
            }

            if (aiContent is not null)
            {
                aiContent.RawRepresentation = item.InnerContent;
                aiContent.AdditionalProperties = item.Metadata is not null ? new AdditionalPropertiesDictionary(item.Metadata) : null;

                message.Contents.Add(aiContent);
            }
        }

        return message;
    }
}