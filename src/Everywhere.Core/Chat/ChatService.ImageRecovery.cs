using Everywhere.Common;
using Microsoft.SemanticKernel.ChatCompletion;

namespace Everywhere.Chat;

partial class ChatService
{
    private async Task<T> RequestWithImageRecoveryAsync<T>(
        ChatContext chatContext,
        GenerationContext generation,
        IReadOnlyList<ChatMessage> messages,
        Func<ChatHistory, Task<T>> request,
        CancellationToken cancellationToken)
    {
        var offloaded = await ChatHistoryBuilder.ReadImageOffloadsAsync(chatContext, generation.GetPath(), cancellationToken);
        while (true)
        {
            var input = await ChatHistoryBuilder.BuildModelInputAsync(
                generation.PromptRenderer,
                generation.SystemPrompt,
                messages,
                generation.InputModalities,
                offloaded,
                cancellationToken,
                chatContext);
            try
            {
                return await request(input.History);
            }
            catch (ChatRequestException error) when (input.SentImages.Count > 0 && IsImageRecoveryFailure(error.FinalFailure))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var latest = input.SentImages[^1];
                // A tool-call span can contain several view_image calls in one provider
                // batch. Keep that batch together on the first retry.
                var olderImages = input.SentImages
                    .AsValueEnumerable()
                    .TakeWhile(image => image.NodeId != latest.NodeId || image.SpanIndex != latest.SpanIndex)
                    .ToArray();
                // If only the newest batch remains, continue with explicit omissions
                // instead of pretending the model received the images.
                var images = olderImages.Length > 0 ? olderImages : input.SentImages;

                await chatContext.DispatchAsync(() => generation.Append(new ChatImageOffloadMessage(images, DateTimeOffset.UtcNow)));
                await _chatContextManager.FlushPendingChatAsync(chatContext, cancellationToken);
                offloaded.UnionWith(images);
            }
        }
    }

    private static bool IsImageRecoveryFailure(HandledChatException failure)
    {
        if (failure is HandledChatException.InvalidRequest.RequestInputTooLarge)
        {
            return true;
        }

        return failure is HandledChatException.InvalidRequest.ContextLengthExceeded &&
            (int?)failure.Diagnostics?.Evidence.StatusCode is null or 400 or 422;
    }
}