using CommunityToolkit.Mvvm.ComponentModel;
using Everywhere.Chat;

namespace Everywhere.Views;

/// <summary>
/// UI-owned branch selector. Reads are captured; a user selection delegates to the data's graph mutation.
/// </summary>
public sealed class ChatBranchSelection : ObservableObject
{
    /// <summary>
    /// Gets the stable command target for editing, retrying and copying.
    /// </summary>
    public ChatMessageNode Node { get; }

    /// <summary>
    /// Gets the captured number of alternatives, including this node.
    /// </summary>
    public int ChoiceCount => _choiceCount;

    /// <summary>
    /// Gets or selects the zero-based alternative at this branch point.
    /// </summary>
    public int ChoiceIndex
    {
        get => _choiceIndex;
        set
        {
            if (value == _choiceIndex || Node.Parent is not { } parent)
            {
                return;
            }

            var children = parent.Children;
            var index = Math.Clamp(value, -1, children.Count - 1);
            var child = index >= 0 ? Node.Data.FindNode(children[index]) : null;
            SetProperty(ref _choiceIndex, index, nameof(ChoiceIndex));
            _context.SelectChildAsync(parent, child).Detach();
        }
    }

    private readonly ChatContext _context;
    private int _choiceCount;
    private int _choiceIndex;

    /// <summary>
    /// Creates a selector for a materialized node without subscribing to the graph.
    /// </summary>
    public ChatBranchSelection(ChatContext context, ChatMessageNode node)
    {
        _context = context;
        Node = node;
        Refresh();
    }

    /// <summary>
    /// Captures the latest relationships during the owning turn's render pass.
    /// </summary>
    public void Refresh()
    {
        SetProperty(ref _choiceCount, Node.Parent?.ChoiceCount ?? 1, nameof(ChoiceCount));
        SetProperty(ref _choiceIndex, Node.Parent?.ChoiceIndex ?? 0, nameof(ChoiceIndex));
    }
}