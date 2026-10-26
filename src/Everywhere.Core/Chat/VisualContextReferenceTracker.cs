namespace Everywhere.Chat;

/// <summary>
/// Reads persisted visual Context references from the messages selected for a provider request.
/// </summary>
internal static class VisualContextReferenceTracker
{
    /// <summary>
    /// Collects the visual Context identities whose Agent-visible references remain active in the selected request context.
    /// </summary>
    /// <remarks>
    /// The selected messages, their Assistant spans, and its function results are visited once in chronological order.
    /// Reset boundaries discard identities from earlier Contexts while retaining references already owned by the
    /// Context named by the boundary.
    /// </remarks>
    public static HashSet<Guid> GetActiveContextIds(IReadOnlyList<ChatMessage> messages)
    {
        var activeContextIds = new HashSet<Guid>();
        foreach (var message in messages)
        {
            VisitMessage(message, activeContextIds);
        }

        return activeContextIds;
    }

    private static void VisitMessage(ChatMessage message, HashSet<Guid> activeContextIds)
    {
        if (message is VisualContextResetChatMessage resetMessage)
        {
            ApplyReset(resetMessage, activeContextIds);
            return;
        }

        AddReference(message, activeContextIds);
        if (message is not AssistantChatMessage assistant) return;

        foreach (var span in assistant.Items)
        {
            switch (span)
            {
                case AssistantChatMessageVisualContextResetSpan resetSpan:
                    ApplyReset(resetSpan.Message, activeContextIds);
                    break;
                case AssistantChatMessageFunctionCallSpan functionCallSpan:
                    foreach (var functionCall in functionCallSpan.Items)
                    {
                        foreach (var result in functionCall.Results)
                        {
                            AddReference(result.Result, activeContextIds);
                        }
                    }
                    break;
            }
        }
    }

    private static void ApplyReset(VisualContextResetChatMessage resetMessage, HashSet<Guid> activeContextIds)
    {
        var hasCurrentReferences = activeContextIds.Contains(resetMessage.VisualContextId);
        activeContextIds.Clear();
        if (hasCurrentReferences) activeContextIds.Add(resetMessage.VisualContextId);
    }

    private static void AddReference(object? value, HashSet<Guid> activeContextIds)
    {
        if (value is IHaveVisualReferences { VisualContextId: { } visualContextId })
        {
            activeContextIds.Add(visualContextId);
        }
    }
}