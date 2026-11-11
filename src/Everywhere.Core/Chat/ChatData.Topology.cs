namespace Everywhere.Chat;

partial class ChatData
{
    internal void ApplyCommittedTopology(ChatNodeTopology[] topology, bool shouldRetainCurrentPath)
    {
        var changedParents = new HashSet<ChatMessageNode>();
        var changedTurns = new HashSet<ChatTurn>();
        var children = topology.AsValueEnumerable().Where(row => row.ParentId is not null).ToLookup(row => row.ParentId);
        var queue = new Queue<ChatMessageNode>();
        queue.Enqueue(RootNode);
        while (queue.TryDequeue(out var parent))
        {
            foreach (var source in children[parent.Id])
            {
                var node = FindNode(source.Id);
                if (node is null)
                {
                    node = new ChatMessageNode(source.Id, source.MessageKind, source.CreatedAt, source.UpdatedAt)
                    {
                        Data = this,
                        Parent = parent,
                    };
                    _messageNodeMap.TryAdd(node.Id, node);
                    AssignTurn(node);
                    parent.AddChild(node.Id);
                    changedParents.Add(parent);
                    if (node.Turn is { } newTurn)
                    {
                        changedTurns.Add(newTurn);
                    }
                }
                if (node.SummaryThroughNodeId != source.SummaryThroughNodeId)
                {
                    node.SummaryThroughNodeId = source.SummaryThroughNodeId;
                    if (node.Turn is { } turn)
                    {
                        changedTurns.Add(turn);
                    }
                }
                queue.Enqueue(node);
            }
        }
        foreach (var row in topology)
        {
            var node = FindNode(row.Id) ?? throw new InvalidDataException("Installed topology has a different root.");
            if (node.ChoiceChildId != row.ChoiceChildId)
            {
                node.SetChoiceChild(row.ChoiceChildId);
                changedParents.Add(node);
            }
        }
        foreach (var parent in changedParents)
        {
            if (parent.Turn is { } turn)
            {
                changedTurns.Add(turn);
            }
            if (GetCurrentChild(parent)?.Turn is { } currentChildTurn)
            {
                changedTurns.Add(currentChildTurn);
            }
            if (parent.ChosenChild?.Turn is { } childTurn)
            {
                changedTurns.Add(childTurn);
            }
        }
        foreach (var turn in changedTurns)
        {
            if (!shouldRetainCurrentPath || !SelectedTurns.Contains(turn))
            {
                RebuildTurnPath(turn);
            }
            turn.Invalidate();
        }
        if (!shouldRetainCurrentPath)
        {
            UpdateBranchAfter(0, RootNode);
        }
        OnPropertyChanged(nameof(HasSyncedBranchUpdate));
        // An unselected new sibling still changes branch controls without changing SelectedNodes.
        if (changedParents.Any(node => node.Turn is null))
        {
            AuxiliaryInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }
}