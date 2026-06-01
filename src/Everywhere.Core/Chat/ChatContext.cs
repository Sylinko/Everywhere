using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.AI;
using Everywhere.Collections;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Common;
using Everywhere.Configuration;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Utilities;
using MessagePack;

namespace Everywhere.Chat;

/// <summary>
/// Maintains the context of the chat, including a tree of <see cref="ChatMessageNode"/> and other metadata.
/// The current branch is derived by following each node's <see cref="ChatMessageNode.ChoiceIndex"/>.
/// </summary>
[MessagePackObject(AllowPrivate = true)]
public sealed partial class ChatContext : ObservableObject, IObservableList<ChatMessageNode>
{
    /// <summary>
    /// Keeps a strong reference to busy chat contexts to prevent them from being garbage collected.
    /// </summary>
    // ReSharper disable once CollectionNeverQueried.Local
    private static readonly HashSet<ChatContext> BusyChatContexts = [];

    [Key(0)]
    public ChatContextMetadata Metadata { get; }

    /// <summary>
    /// Items in the current branch, excluding the root system prompt node. Used for UI bindings.
    /// </summary>
    [IgnoreMember]
    public IReadOnlyBindableList<ChatMessageNode> DisplayItems { get; }

    /// <summary>
    /// Messages in the current branch.
    /// </summary>
    [IgnoreMember]
    public int Count => _branchNodesSourceList.Count;

    [IgnoreMember]
    public IObservable<int> CountChanged => _branchNodesSourceList.CountChanged;

    [IgnoreMember]
    public IReadOnlyList<ChatMessageNode> Items => _branchNodesSourceList.Items;

    /// <summary>
    /// Key: VisualElement.Id
    /// Value: VisualElement.
    /// VisualElement is dynamically created and not serialized, so we keep a map here to track them.
    /// This is also not serialized.
    /// </summary>
    [IgnoreMember]
    public ResilientCache<int, IVisualElement> VisualElements { get; private init; } = new();

    /// <summary>
    /// A map of granted permissions for plugin functions in this chat context (session).
    /// Key: PluginName.FunctionName
    /// Value: is granted or not.
    /// </summary>
    [IgnoreMember]
    public ConcurrentDictionary<string, bool> IsPermissionGrantedRecords { get; private init; } = new();

    /// <summary>
    /// Tool and plugin rulesets for this chat context. This is used to determine which plugins and functions are enabled or disabled in this context.
    /// </summary>
    [IgnoreMember]
    public ToolRulesets? ToolRulesets { get; set; }

    [IgnoreMember]
    public AsyncLocal<FunctionCallContext?> FunctionCallContext { get; } = new();

    /// <summary>
    /// Indicates whether the chat context is currently busy waiting for a response. This can be used to disable user input and show a loading indicator in the UI.
    /// The busy state can be entered by calling <see cref="TryExecute"/>.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Resource key for the busy message to show when waiting for a response.
    /// This can be set temporarily using <see cref="SetBusyMessage(IDynamicResourceKey?)"/>.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial IDynamicResourceKey? BusyMessageKey { get; private set; }

    #region UserInterface

    [IgnoreMember]
    public IReadOnlyBindableList<ChatPluginUserInterfaceItem> ChatPluginUserInterfaceItems { get; }

    [IgnoreMember]
    [ObservableProperty]
    public partial IReadOnlyList<ChatPluginTodoItem>? TodoItems { get; set; }

    #endregion

    /// <summary>
    /// Backing store for MessagePack (de)serialization: nodes are persisted as a collection, and linked by Ids.
    /// </summary>
    [Key(1)]
    private ICollection<ChatMessageNode> MessageNodes => _messageNodeMap.Values;

    /// <summary>
    /// Root node (Guid.Empty) which is important for branch resolution but not included in the message node map.
    /// </summary>
    [Key(2)]
    private readonly ChatMessageNode _rootNode;

    /// <summary>
    /// Map of all message nodes by their ID. This allows for quick access to any node in the context.
    /// NOTE that this map does not include the root node, which is always at Id = Guid.Empty.
    /// </summary>
    [IgnoreMember] private readonly Dictionary<Guid, ChatMessageNode> _messageNodeMap = new();

    /// <summary>
    /// Nodes on the currently selected branch. [0] is always the root node.
    /// </summary>
    [IgnoreMember] private readonly SourceList<ChatMessageNode> _branchNodesSourceList = new();
    [IgnoreMember] private readonly SourceList<ChatPluginUserInterfaceItem> _chatPluginUserInterfaceItemsSourceList = new();
    [IgnoreMember] private readonly IDisposable _displayItemsSubscription;
    [IgnoreMember] private readonly IDisposable _chatPluginUserInterfaceItemsSubscription;
    [IgnoreMember] private readonly IDisposable _metadataSyncSubscription;

    /// <summary>
    /// Constructor for MessagePack deserialization and for creating a new chat context with existing nodes.
    /// </summary>
    /// <param name="metadata"></param>
    /// <param name="messageNodes"></param>
    /// <param name="rootNode"></param>
    [SerializationConstructor]
    public ChatContext(ChatContextMetadata metadata, ICollection<ChatMessageNode> messageNodes, ChatMessageNode rootNode)
    {
        Metadata = metadata;
        _messageNodeMap.AddRange(messageNodes.Select(v => new KeyValuePair<Guid, ChatMessageNode>(v.Id, v)));
        _rootNode = rootNode;
        _branchNodesSourceList.Add(rootNode);

        foreach (var node in messageNodes.Append(rootNode))
        {
            node.Context = this;
            node.PropertyChanged += HandleNodePropertyChanged;
            foreach (var childId in node.Children) _messageNodeMap[childId].Parent = node;
        }

        if (_messageNodeMap.ContainsKey(Guid.Empty))
            throw new InvalidOperationException("Message nodes cannot contain a node with an empty ID.");

        UpdateBranchAfter(0, rootNode);

        DisplayItems = _branchNodesSourceList
            .Connect()
            .Filter(node => node != rootNode)
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _displayItemsSubscription);
        ChatPluginUserInterfaceItems = _chatPluginUserInterfaceItemsSourceList
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _chatPluginUserInterfaceItemsSubscription);
        _metadataSyncSubscription = _chatPluginUserInterfaceItemsSourceList.CountChanged
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(count =>
            {
                if (count > 0) Metadata.States |= ChatContextMetadataStates.HasNotification;
                else Metadata.States &= ~ChatContextMetadataStates.HasNotification;
            });
    }

    /// <summary>
    /// Creates a new chat context. A new Guid v7 ID is assigned.
    /// </summary>
    public ChatContext()
    {
        Metadata = new ChatContextMetadata(Guid.CreateVersion7(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        _rootNode = new ChatMessageNode(Guid.CreateVersion7().SetVersion(0), new RootChatMessage());
        _rootNode.PropertyChanged += HandleNodePropertyChanged;
        _branchNodesSourceList.Add(_rootNode);

        DisplayItems = _branchNodesSourceList
            .Connect()
            .Filter(node => node != _rootNode)
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _displayItemsSubscription);
        ChatPluginUserInterfaceItems = _chatPluginUserInterfaceItemsSourceList
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(out _chatPluginUserInterfaceItemsSubscription);
        _metadataSyncSubscription = _chatPluginUserInterfaceItemsSourceList.CountChanged
            .ObserveOnAvaloniaDispatcher()
            .Subscribe(count =>
            {
                if (count > 0) Metadata.States |= ChatContextMetadataStates.HasNotification;
                else Metadata.States &= ~ChatContextMetadataStates.HasNotification;
            });
    }

    #region Busy implementation

    [IgnoreMember]
    private readonly ReusableCancellationTokenSource _cancellationTokenSource = new();

    /// <summary>
    /// Try to execute a task in a busy state. If the context is already busy, returns false.
    /// This method is only safe to call on the UI thread.
    /// Note that action is executed with Task.Run
    /// </summary>
    public bool TryExecute(Func<CancellationToken, Task> action, IExceptionHandler exceptionHandler)
    {
        Dispatcher.UIThread.VerifyAccess();

        if (IsBusy) return false;

        IsBusy = true;
        BusyChatContexts.Add(this);
        var cancellationToken = _cancellationTokenSource.Token;

        Task.Run(() => action(cancellationToken), cancellationToken)
            .ContinueWith(
                t =>
                {
                    Debug.Assert(Dispatcher.UIThread.CheckAccess());

                    BusyChatContexts.Remove(this);
                    IsBusy = false;
                    if (t.Exception is { } exception) exceptionHandler.HandleException(exception.InnerException ?? exception);
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.FromCurrentSynchronizationContext());

        return true;
    }

    /// <summary>
    /// Cancels the current task if the context is busy.
    /// </summary>
    public void Cancel()
    {
        _cancellationTokenSource.Cancel();
    }

    #endregion

    /// <summary>
    /// Fork a new ChatContext that inherits the current branch and metadata, but has a new Guid v7 ID and is marked as temporary.
    /// This is useful for running sub-agents in a separate context while maintaining the same VisualElements and permissions.
    /// </summary>
    /// <returns></returns>
    public ChatContext ForkSubagent()
    {
        return new ChatContext
        {
            Metadata =
            {
                IsTemporary = true
            },
            VisualElements = VisualElements,
            IsPermissionGrantedRecords = IsPermissionGrantedRecords,
            ToolRulesets = ToolRulesets.Copy(new ToolRulesets(2)
            {
                { "builtin.essential.run_subagent", false }, // Disallow run_subagent in sub-agents to prevent infinite recursion
                { "builtin.essential.ask_user_question", false } // Disallow ask_user_question in sub-agents to prevent user interaction in sub-agents
            })
        };
    }

    /// <summary>
    /// Create a new branch on the specified sibling node by inserting a new message at that position.
    /// </summary>
    public void CreateBranchOn(ChatMessageNode siblingNode, ChatMessage chatMessage)
    {
        var index = _branchNodesSourceList.Items.IndexOf(siblingNode);
        var afterNode = index switch
        {
            < 0 => throw new ArgumentException("The specified node is not in the current branch.", nameof(siblingNode)),
            0 => _rootNode,
            _ => _branchNodesSourceList.Items[index - 1]
        };

        var newNode = new ChatMessageNode(chatMessage)
        {
            Context = this,
            Parent = afterNode,
        };
        newNode.PropertyChanged += HandleNodePropertyChanged;
        _messageNodeMap[newNode.Id] = newNode;

        afterNode.Add(newNode.Id);
        afterNode.ChoiceIndex = afterNode.Children.Count - 1;

        UpdateBranchAfter(index - 1, afterNode);
    }

    public void Insert(int index, ChatMessage chatMessage) => Insert(index, new ChatMessageNode(chatMessage) { Context = this });

    /// <summary>
    /// Adds a message at the end of the current branch.
    /// </summary>
    public void Add(ChatMessage message)
    {
        Insert(_branchNodesSourceList.Count, new ChatMessageNode(message) { Context = this });
    }

    /// <summary>
    /// Gets all nodes in the chat context in all branches, including the root node.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<ChatMessageNode> GetAllNodes()
    {
        yield return _rootNode;
        foreach (var node in _messageNodeMap.Values)
        {
            yield return node;
        }
    }

    /// <summary>
    /// Sets the busy message resource key for the duration of the returned IDisposable.
    /// </summary>
    /// <param name="busyMessage"></param>
    /// <returns></returns>
    public IDisposable SetBusyMessage(IDynamicResourceKey? busyMessage)
    {
        var previous = BusyMessageKey;
        BusyMessageKey = busyMessage;
        return Disposable.Create(() => BusyMessageKey = previous);
    }

    public IObservable<IChangeSet<ChatMessageNode>> Connect(Func<ChatMessageNode, bool>? predicate = null) =>
        _branchNodesSourceList.Connect(predicate);

    public IObservable<IChangeSet<ChatMessageNode>> Preview(Func<ChatMessageNode, bool>? predicate = null) =>
        _branchNodesSourceList.Preview(predicate);

    private void HandleNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChatMessageNode.ChoiceIndex))
        {
            UpdateBranchAfterNode(sender.NotNull<ChatMessageNode>());
        }

        Metadata.DateModified = DateTimeOffset.UtcNow;
        WeakReferenceMessenger.Default.Send(new ChatContextMetadataChangedMessage(this, Metadata, nameof(Metadata.DateModified)));
    }

    /// <summary>
    /// Rebuilds the current branch from the specified node forward.
    /// </summary>
    private void UpdateBranchAfterNode(ChatMessageNode node) => UpdateBranchAfter(_branchNodesSourceList.Items.IndexOf(node), node);

    private void UpdateBranchAfter(int index, ChatMessageNode node)
    {
        if (index == -1)
            throw new ArgumentOutOfRangeException(nameof(index), "Node is not in the branch nodes.");

        for (var i = _branchNodesSourceList.Count - 1; i > index; i--) _branchNodesSourceList.RemoveAt(i);

        // Follow ChoiceIndex down the tree.
        while (true)
        {
            if (node.ChoiceIndex < 0 || node.ChoiceIndex >= node.Children.Count) break;
            _branchNodesSourceList.Add(node = _messageNodeMap[node.Children[node.ChoiceIndex]]);
        }
    }

    private void Insert(int index, ChatMessageNode newNode)
    {
        if (newNode.Id == Guid.Empty)
            throw new ArgumentException("New node must have a non-empty ID.", nameof(newNode));

        _messageNodeMap[newNode.Id] = newNode;
        newNode.PropertyChanged += HandleNodePropertyChanged;

        var afterNode = index switch
        {
            0 => _rootNode,
            _ => _branchNodesSourceList.Items[index - 1]
        };

        if (afterNode.Children.Count > 0)
        {
            newNode.AddRange(afterNode.Children);
            newNode.ChoiceIndex = afterNode.ChoiceIndex;
            foreach (var afterNodeChildId in afterNode.Children)
            {
                _messageNodeMap[afterNodeChildId].Parent = newNode;
            }

            afterNode.Clear();
        }

        newNode.Parent = afterNode;
        afterNode.Add(newNode.Id);

        UpdateBranchAfter(index - 1, afterNode);
    }

    public void Dispose()
    {
        foreach (var node in _messageNodeMap.Values)
        {
            node.PropertyChanged -= HandleNodePropertyChanged;
            node.Dispose();
        }

        _rootNode.PropertyChanged -= HandleNodePropertyChanged;
        _rootNode.Dispose();
        _chatPluginUserInterfaceItemsSubscription.Dispose();
        _displayItemsSubscription.Dispose();
        _metadataSyncSubscription.Dispose();
        _branchNodesSourceList.Dispose();

        Dispatcher.UIThread.Post(() => Metadata.States = ChatContextMetadataStates.None);

        GC.SuppressFinalize(this);
    }

    ~ChatContext()
    {
        Dispatcher.UIThread.Post(() => Metadata.States = ChatContextMetadataStates.None);
    }

    partial void OnIsBusyChanged(bool value)
    {
        Dispatcher.UIThread.PostOnDemand(() =>
        {
            if (value) Metadata.States |= ChatContextMetadataStates.Busy;
            else Metadata.States &= ~ChatContextMetadataStates.Busy;
        });
    }

    public async Task<ConsentDecisionResult> HandleConsentRequestAsync(
        IDynamicResourceKey headerKey,
        ChatPluginDisplayBlock? content,
        RequestConsentRememberMasks rememberMasks,
        CancellationToken cancellationToken)
    {
        var item = new ChatPluginUserInterfaceConsentRequestItem(headerKey, content, rememberMasks, cancellationToken);
        _chatPluginUserInterfaceItemsSourceList.Add(item);
        WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(item.HeaderKey.ToString()));

        try
        {
            return await item.Task;
        }
        finally
        {
            _chatPluginUserInterfaceItemsSourceList.Remove(item);
        }
    }

    public async Task<IReadOnlyList<ChatPluginQuestionAnswer>> AskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        var item = new ChatPluginUserInterfaceAskQuestionItem(questions, cancellationToken);
        _chatPluginUserInterfaceItemsSourceList.Add(item);
        WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(item.Questions.FirstOrDefault()?.Question));

        try
        {
            return await item.Task;
        }
        finally
        {
            _chatPluginUserInterfaceItemsSourceList.Remove(item);
        }
    }

    /// <summary>
    /// Get and ensures the working directory
    /// </summary>
    /// <returns>
    /// Usually a temporary directory path like C:\Users\[UserName]\AppData\Roaming\Everywhere\plugins\2025-12-30
    /// </returns>

    public string EnsureWorkingDirectory() =>
        RuntimeConstants.EnsureWritableDataFolderPath("plugins", Metadata.DateCreated.ToString("yyyy-MM-dd"));

    public IDictionary<string, Func<string>> GetPromptVariables()
    {
        return new Dictionary<string, Func<string>>(
        [
            new KeyValuePair<string, Func<string>>("Date", () => DateTime.Now.ToString("D")),
            new KeyValuePair<string, Func<string>>("Time", () => DateTime.Now.ToString("F")),
            new KeyValuePair<string, Func<string>>("OS", () => Environment.OSVersion.ToString()),
            new KeyValuePair<string, Func<string>>("SystemLanguage", () => LocaleManager.CurrentLocale.ToEnglishName()),
            new KeyValuePair<string, Func<string>>("WorkingDirectory", EnsureWorkingDirectory),
            new KeyValuePair<string, Func<string>>("DefaultSystemPrompt", () => Prompts.DefaultSystemPrompt),
        ]);
    }
}
