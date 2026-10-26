using Everywhere.Chat.Plugins;

namespace Everywhere.Chat;

/// <summary>
/// Enumerates durable resources owned by messages, including embedded child conversations.
/// Tool-visible arbitrary path strings and file-reference display labels are not managed blobs.
/// </summary>
public static class ChatMessageResources
{
    /// <summary>
    /// Gets embedded child conversation data without consulting any UI projection.
    /// </summary>
    public static IEnumerable<ChatData> GetChildData(ChatMessage message)
    {
        var calls = message switch
        {
            FunctionCallChatMessage call => [call],
            AssistantChatMessage assistant => assistant.Items.OfType<AssistantChatMessageFunctionCallSpan>().SelectMany(span => span.Items),
            _ => [],
        };

        foreach (var call in calls)
        {
            foreach (var childData in GetChildData(call.DisplayBlocks))
            {
                yield return childData;
            }
        }
    }

    /// <summary>
    /// Gets file attachments from a fully materialized message and its embedded child conversations.
    /// </summary>
    public static IEnumerable<FileAttachment> GetFileAttachments(ChatMessage message)
    {
        if (message is IHaveChatAttachments attachments)
        {
            foreach (var attachment in attachments.Attachments.OfType<FileAttachment>())
            {
                yield return attachment;
            }
        }

        foreach (var childData in GetChildData(message))
        {
            foreach (var node in childData.GetAllNodes())
            {
                foreach (var attachment in GetFileAttachments(node.Message))
                {
                    yield return attachment;
                }
            }
        }
    }

    private static IEnumerable<ChatData> GetChildData(IEnumerable<ChatPluginDisplayBlock> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is ChatPluginSubagentDisplayBlock child)
            {
                yield return child.ChatData;
            }
            else if (block is ChatPluginContainerDisplayBlock container)
            {
                foreach (var childData in GetChildData(container))
                {
                    yield return childData;
                }
            }
        }
    }
}