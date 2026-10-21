using Everywhere.AI;
using Everywhere.Statistics;

namespace Everywhere.Chat;

public interface IChatService
{
    /// <summary>
    /// Send a message to the chat service. This method is NOT thread safe.
    /// </summary>
    /// <param name="chatContext">The destination captured when the draft was submitted.</param>
    /// <param name="message"></param>
    bool SendMessage(ChatContext chatContext, UserChatMessage message);

    /// <summary>
    /// Sends using the supplied assistant without changing the global assistant selection.
    /// Returns false when the destination is busy; ownership of the message's attachments then stays with the caller.
    /// Accepted messages may carry acquisition-context anchors, which are moved during visual processing.
    /// This method is only safe to call on the UI thread.
    /// </summary>
    bool SendMessage(ChatContext chatContext, UserChatMessage message, CustomAssistant? customAssistant);

    /// <summary>
    /// Edit a previously sent user message. This will create a branch in the chat history. This method is NOT thread safe.
    /// </summary>
    /// <param name="oldNode"></param>
    /// <param name="newMessage"></param>
    bool Edit(ChatMessageNode oldNode, UserChatMessage newMessage);

    /// <summary>
    /// Retry sending a message that previously failed. This will create a branch in the chat history. This method is NOT thread safe.
    /// </summary>
    /// <param name="node"></param>
    void Retry(ChatMessageNode node);

    /// <summary>
    /// Continues generating a response for a given chat message node. This appends to the current branch. This method is NOT thread safe.
    /// </summary>
    /// <param name="node"></param>
    void Continue(ChatMessageNode node);

    /// <summary>
    /// Replaces the current conversation prefix with a persisted compression summary.
    /// This method is NOT thread safe.
    /// </summary>
    void CompactContext();

    /// <summary>
    /// Generates a response for the given chat context and assistant chat message.
    /// </summary>
    /// <param name="chatContext"></param>
    /// <param name="assistant"></param>
    /// <param name="assistantNode">The current branch node containing the assistant response.</param>
    /// <param name="compressionBoundary">The initial prefix eligible for compression before this response produces output.</param>
    /// <param name="systemPromptOverride"></param>
    /// <param name="enableNotifications">Send notifications for function calls and other events during the generation process.</param>
    /// <param name="purpose">Statistics classification for model invocations produced by this generation.</param>
    /// <param name="cancellationToken"></param>
    Task<ChatGenerationResult> GenerateAsync(
        ChatContext chatContext,
        Assistant assistant,
        ChatMessageNode assistantNode,
        CompressionBoundary compressionBoundary,
        string? systemPromptOverride = null,
        bool enableNotifications = true,
        StatisticsModelInvocationPurpose purpose = StatisticsModelInvocationPurpose.ChatResponse,
        CancellationToken cancellationToken = default);
}
