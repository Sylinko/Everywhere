using Everywhere.AI;
using Everywhere.Harness;
using Everywhere.Statistics;

namespace Everywhere.Chat;

public interface IChatService
{
    /// <summary>
    /// Submits a user message and completes after it enters the graph, without waiting
    /// for generation. Returns false when admission is rejected. On rejection or initialization
    /// failure the composer retains its input. This method is UI-thread-only.
    /// </summary>
    /// <param name="chatContext">The destination captured when the draft was submitted.</param>
    /// <param name="message"></param>
    /// <param name="customAssistant">The assistant captured for this submission; null produces a missing-assistant response.</param>
    Task<bool> SendMessageAsync(ChatContext chatContext, UserChatMessage message, CustomAssistant? customAssistant);

    /// <summary>
    /// Submits a user message directly or, when <paramref name="chatContext"/> is busy, enqueues it into
    /// <see cref="ChatContext.PendingInputs"/> using <paramref name="deliveryMode"/> (<see cref="ChatInputDeliveryMode.Queue"/> or <see cref="ChatInputDeliveryMode.Steer"/>).
    /// Returns false only if admission is rejected when idle and cannot be enqueued.
    /// </summary>
    Task<bool> SubmitMessageAsync(
        ChatContext chatContext,
        UserChatMessage message,
        CustomAssistant? customAssistant,
        ChatInputDeliveryMode deliveryMode);

    /// <summary>
    /// Starts processing any queued user messages or pending background job wake notifications on <paramref name="chatContext"/> when idle.
    /// </summary>
    bool ResumePendingInputs(ChatContext chatContext);

    /// <summary>
    /// Gets whether <paramref name="chatContext"/> has paused pending inputs or an interrupted/unanswered turn that can be resumed.
    /// </summary>
    bool CanResume(ChatContext chatContext);

    /// <summary>
    /// Resumes an interrupted/unanswered turn or paused pending queue on <paramref name="chatContext"/>.
    /// </summary>
    void Resume(ChatContext chatContext);

    /// <summary>
    /// Deletes a pending queued user input and disposes any ephemeral visual attachments it held.
    /// </summary>
    bool RemovePendingInput(ChatContext chatContext, Guid itemId);

    /// <summary>
    /// Clears all pending user inputs in <paramref name="chatContext"/> and disposes their ephemeral attachments.
    /// </summary>
    void ClearPendingInputs(ChatContext chatContext);

    /// <summary>
    /// Creates an edited branch and completes after its user message enters the graph, without
    /// waiting for generation. Returns false when admission is rejected. The composer retains
    /// its input on rejection or initialization failure. This method is UI-thread-only.
    /// </summary>
    /// <param name="chatContext">The owning context captured when editing began.</param>
    /// <param name="oldNode"></param>
    /// <param name="newMessage"></param>
    Task<bool> EditAsync(ChatContext chatContext, ChatMessageNode oldNode, UserChatMessage newMessage);

    /// <summary>
    /// Retry sending a message that previously failed. This will create a branch in the chat history. This method is NOT thread safe.
    /// </summary>
    /// <param name="chatContext">The context owning the supplied node.</param>
    /// <param name="node"></param>
    void Retry(ChatContext chatContext, ChatMessageNode node);

    /// <summary>
    /// Continues generating a response for a given chat message node. This appends to the current branch. This method is NOT thread safe.
    /// </summary>
    /// <param name="chatContext">The context owning the supplied node.</param>
    /// <param name="node"></param>
    void Continue(ChatContext chatContext, ChatMessageNode node);

    /// <summary>
    /// Replaces the current conversation prefix with a persisted compression summary.
    /// This method is NOT thread safe.
    /// </summary>
    void CompactContext();

    /// <summary>
    /// Generates a response for the given chat context and assistant chat message.
    /// </summary>
    /// <param name="chatContext">The conversation context owning this operation.</param>
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