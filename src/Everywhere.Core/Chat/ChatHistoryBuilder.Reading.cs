using Everywhere.Prompting;

namespace Everywhere.Chat;

partial class ChatHistoryBuilder
{
    /// <summary>
    /// Selects provider input from an execution path using lightweight topology before loading bodies.
    /// Compression callers may append earlier valid checkpoints to a bounded source prefix.
    /// </summary>
    public static async Task<ChatHistorySelection> SelectContextMessagesAsync(
        ChatContext context,
        IReadOnlyList<ChatMessageNode> path,
        int maxContextRounds,
        int declaredContextLimit,
        CancellationToken cancellationToken = default)
    {
        var positions = new Dictionary<Guid, int>(path.Count);
        for (var index = 0; index < path.Count; index++)
        {
            positions.Add(path[index].Id, index);
        }

        ChatMessageNode? summaryNode = null;
        var coveredIndex = -1;
        for (var index = path.Count - 1; index >= 0; index--)
        {
            var node = path[index];
            if (node.SummaryThroughNodeId is not { } coveredId)
            {
                continue;
            }

            if (coveredId == Guid.Empty && !positions.ContainsKey(Guid.Empty))
            {
                coveredIndex = -1;
            }
            else if (!positions.TryGetValue(coveredId, out coveredIndex) || coveredIndex >= index)
            {
                continue;
            }

            summaryNode = node;
            break;
        }

        var suffix = path.Skip(summaryNode is null ? 0 : coveredIndex + 1)
            .Where(node => node.MessageKind is not (ChatMessageKind.Root or ChatMessageKind.Compression))
            .ToArray();
        var startIndex = 0;
        if (maxContextRounds >= 0 && suffix.Length > 0)
        {
            var turns = new HashSet<Guid>();
            for (var index = suffix.Length - 1; index >= 0; index--)
            {
                if (suffix[index].Turn is { } turn && turns.Add(turn.Id) && turns.Count > (long)maxContextRounds + 1)
                {
                    startIndex = index + 1;
                    break;
                }
            }
        }

        var requiredNodes = suffix.Skip(startIndex).ToList();
        if (summaryNode is not null)
        {
            requiredNodes.Add(summaryNode);
        }

        var requiredTurns = requiredNodes.Select(node => node.Turn).OfType<ChatTurn>().Distinct();
        foreach (var turn in requiredTurns)
        {
            await context.EnsureTurnLoadedAsync(turn, cancellationToken);
        }

        if (requiredNodes.Any(node => node.Turn is null && !node.IsMessageLoaded))
        {
            await context.EnsureTurnLoadedAsync(null, cancellationToken);
        }

        var messages = new List<ChatMessage>();
        if (summaryNode is not null)
        {
            var remainingTokens = declaredContextLimit > 0 ?
                Math.Min(MaximumRetainedUserMessageTokens, (int)(declaredContextLimit * RetainedUserMessageContextRatio)) :
                0;
            for (var index = coveredIndex; index >= 0 && remainingTokens > 0; index--)
            {
                var node = path[index];
                if (node.MessageKind is not (ChatMessageKind.User or ChatMessageKind.UserStrategy))
                {
                    continue;
                }

                var content = await context.ReadUserTextAsync(node, cancellationToken);
                var tokens = TokenHelper.EstimateTokenCount(content);
                if (tokens <= remainingTokens)
                {
                    messages.Add(new UserChatMessage(content, []));
                    remainingTokens -= tokens;
                    continue;
                }

                var retainedContent = TokenHelper.Omit(content, remainingTokens, RetainedUserMessageOmissionMarker);
                if (!string.IsNullOrWhiteSpace(retainedContent))
                {
                    messages.Add(new UserChatMessage(retainedContent, []));
                }

                break;
            }

            messages.Reverse();
            messages.Add(summaryNode.Message);
        }

        messages.AddRange(suffix.Skip(startIndex).Select(node => node.Message));
        return new ChatHistorySelection(messages, startIndex > 0);
    }
}

/// <summary>
/// The selected summary, retained user text and raw suffix, plus whether the round limit omitted input.
/// </summary>
public sealed record ChatHistorySelection(IReadOnlyList<ChatMessage> Messages, bool WasHistoryTrimmed);