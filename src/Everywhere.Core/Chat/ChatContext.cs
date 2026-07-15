using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using DynamicData;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Interop;
using Everywhere.Messages;
using Everywhere.Utilities;
using Everywhere.Views;
using Lucide.Avalonia;
using MessagePack;
using Serilog;
using ZLinq;

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
    /// Gets the non-serialized presentation companion for this context. It is created lazily so
    /// nested or background chat contexts pay no projection cost until a view or runtime activity
    /// actually needs it. Keeping the companion here preserves row identity, expansion state, and
    /// first-presentation state when a chat view is detached and later reattached.
    /// </summary>
    [IgnoreMember]
    public ChatPresentation Presentation
    {
        get
        {
            using var _ = _presentationLock.EnterScope();
            if (_isDisposed) throw new ObjectDisposedException(nameof(ChatContext));
            return _presentation ??= new ChatPresentation(this);
        }
    }

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
    public ResilientCache<int, IVisualElement> VisualElements { get; }

    /// <summary>
    /// A map of granted permissions for plugin functions in this chat context (session).
    /// Key: PluginName.FunctionName
    /// Value: is granted or not.
    /// </summary>
    [IgnoreMember]
    public ConcurrentDictionary<string, bool> IsPermissionGrantedRecords { get; }

    /// <summary>
    /// Tool and plugin rulesets for this chat context. This is used to determine which plugins and functions are enabled or disabled in this context.
    /// </summary>
    [IgnoreMember]
    public ToolRulesets? ToolRulesets { get; }

    [IgnoreMember]
    public AsyncLocal<FunctionCallContext?> FunctionCallContext { get; } = new();

    /// <summary>
    /// Enters one invocation context for the current asynchronous execution flow and restores the
    /// previous value when the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Restoring the captured value, rather than assigning <see langword="null"/>, is essential for
    /// nested tool execution. AsyncLocal then carries each invocation independently when sibling
    /// operations are scheduled in separate execution contexts.
    /// </remarks>
    internal IDisposable EnterFunctionCallContext(FunctionCallContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var previous = FunctionCallContext.Value;
        FunctionCallContext.Value = context;
        return Disposable.Create(() => FunctionCallContext.Value = previous);
    }

    [IgnoreMember]
    public IChatPluginUserInterfaceBroker UserInterfaceBroker { get; }

    /// <summary>
    /// Indicates whether the chat context is currently busy waiting for a response. This can be used to disable user input and show a loading indicator in the UI.
    /// The busy state can be entered by calling <see cref="TryExecute"/>.
    /// </summary>
    [IgnoreMember]
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>
    /// Backing store for MessagePack (de)serialization: nodes are persisted as a collection, and linked by Ids.
    /// </summary>
    [Key(1)]
    private ICollection<ChatMessageNode> MessageNodes
    {
        get
        {
            using var _ = _graphMutationLock.EnterScope();

            return _messageNodeMap.Values.AsValueEnumerable().ToList();
        }
    }

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
    [IgnoreMember] private readonly Lock _graphMutationLock = new();
    [IgnoreMember] private readonly Lock _presentationLock = new();
    [IgnoreMember] private ChatPresentation? _presentation;

    /// <summary>
    /// Nodes on the currently selected branch. [0] is always the root node.
    /// </summary>
    [IgnoreMember] private readonly SourceList<ChatMessageNode> _branchNodesSourceList = new();
    [IgnoreMember] private readonly CompositeDisposable _disposables = new(2);

    [IgnoreMember] private bool _isDisposed;

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
            .BindEx(_disposables);

        VisualElements = new ResilientCache<int, IVisualElement>();
        IsPermissionGrantedRecords = new ConcurrentDictionary<string, bool>();
        UserInterfaceBroker = new ChatPluginUserInterfaceBroker(this).DisposeWith(_disposables);
    }

    /// <summary>
    /// Creates a new chat context. A new Guid v7 ID is assigned.
    /// </summary>
    public ChatContext() : this(null) { }

    private ChatContext(
        ResilientCache<int, IVisualElement>? visualElements = null,
        ConcurrentDictionary<string, bool>? isPermissionGrantedRecords = null,
        ToolRulesets? toolRulesets = null,
        IChatPluginUserInterfaceBroker? userInterfaceBroker = null)
    {
        Metadata = new ChatContextMetadata(Guid.CreateVersion7(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        VisualElements = visualElements ?? new ResilientCache<int, IVisualElement>();
        IsPermissionGrantedRecords = isPermissionGrantedRecords ?? new ConcurrentDictionary<string, bool>();
        ToolRulesets = toolRulesets;

        _rootNode = new ChatMessageNode(Guid.CreateVersion7().SetVersion(0), new RootChatMessage())
        {
            Context = this
        };
        _rootNode.PropertyChanged += HandleNodePropertyChanged;
        _branchNodesSourceList.Add(_rootNode);

        if (_messageNodeMap.ContainsKey(Guid.Empty))
            throw new InvalidOperationException("Message nodes cannot contain a node with an empty ID.");

        UpdateBranchAfter(0, _rootNode);

        DisplayItems = _branchNodesSourceList
            .Connect()
            .Filter(node => node != _rootNode)
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);

        UserInterfaceBroker = userInterfaceBroker ?? new ChatPluginUserInterfaceBroker(this).DisposeWith(_disposables);
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
        return new ChatContext(
            VisualElements,
            IsPermissionGrantedRecords,
            ToolRulesets.Copy(
                new ToolRulesets(2)
                {
                    { "builtin.essential.run_subagent", false }, // Disallow run_subagent in sub-agents to prevent infinite recursion
                    {
                        "builtin.essential.ask_user_question", false
                    } // Disallow ask_user_question in sub-agents to prevent user interaction in sub-agents
                }),
            UserInterfaceBroker)
        {
            Metadata =
            {
                IsTemporary = true
            }
        };
    }

    /// <summary>
    /// Create a new branch on the specified sibling node by inserting a new message at that position.
    /// </summary>
    public void CreateBranchOn(ChatMessageNode siblingNode, ChatMessage chatMessage)
    {
        using var _ = _graphMutationLock.EnterScope();

        var index = 0;
        ChatMessageNode? afterNode = null;
        _branchNodesSourceList.Edit(list =>
        {
            index = list.IndexOf(siblingNode);
            afterNode = index switch
            {
                < 0 => throw new ArgumentException("The specified node is not in the current branch.", nameof(siblingNode)),
                0 => _rootNode,
                _ => list[index - 1]
            };
        });

        if (afterNode is null) return;

        var newNode = new ChatMessageNode(chatMessage)
        {
            Context = this,
            Parent = afterNode,
        };
        newNode.PropertyChanged += HandleNodePropertyChanged;
        _messageNodeMap[newNode.Id] = newNode;

        afterNode.Add(newNode.Id);
        afterNode.ChoiceIndex = afterNode.Children.Count - 1;

        UpdateBranchAfterCore(index - 1, afterNode);
    }

    /// <summary>
    /// Adds a message at the end of the current branch.
    /// </summary>
    public void Add(ChatMessage message)
    {
        using var _ = _graphMutationLock.EnterScope();

        var index = _branchNodesSourceList.Count;
        var newNode = new ChatMessageNode(message) { Context = this };

        ChatMessageNode afterNode = null!;
        _branchNodesSourceList.Edit(list =>
        {
            afterNode = index switch
            {
                0 => _rootNode,
                _ => list[index - 1]
            };
        });

        _messageNodeMap[newNode.Id] = newNode;

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
        newNode.PropertyChanged += HandleNodePropertyChanged;
        afterNode.Add(newNode.Id);

        UpdateBranchAfterCore(index - 1, afterNode);
    }

    /// <summary>
    /// Gets all nodes in the chat context in all branches, including the root node.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<ChatMessageNode> GetAllNodes()
    {
        using var _ = _graphMutationLock.EnterScope();

        var results = new ChatMessageNode[_messageNodeMap.Count + 1];
        results[0] = _rootNode;
        _messageNodeMap.Values.CopyTo(results, 1);
        return results;
    }

    /// <summary>
    /// Gets a snapshot of the chat context for persistence, including all nodes in all branches and their relationships.
    /// </summary>
    /// <returns></returns>
    public IReadOnlyList<PersistenceNodeSnapshot> GetPersistenceSnapshot()
    {
        using var _ = _graphMutationLock.EnterScope();

        var snapshots = new List<PersistenceNodeSnapshot>(_messageNodeMap.Count + 1) { CreatePersistenceNodeSnapshot(_rootNode) };
        snapshots.AddRange(_messageNodeMap.Values.Select(CreatePersistenceNodeSnapshot));
        return snapshots;

        static PersistenceNodeSnapshot CreatePersistenceNodeSnapshot(ChatMessageNode node)
        {
            var children = node.Children;
            var choiceIndex = node.ChoiceIndex;
            var choiceChildId = choiceIndex >= 0 && choiceIndex < children.Count ? children[choiceIndex] : (Guid?)null;

            return new PersistenceNodeSnapshot(
                node.Id,
                node.Parent?.Id,
                choiceChildId,
                node.Message,
                node.DateModified);
        }
    }

    /// <summary>
    /// Adds a non-persisted activity to the current assistant turn for the lifetime of the returned
    /// scope. Disposing the scope completes the activity while retaining its presentation row in
    /// the current in-memory chronology.
    /// </summary>
    /// <param name="icon">The reliable icon describing the operation category.</param>
    /// <param name="headerKey">The localized running activity title.</param>
    /// <returns>A scope whose disposal marks the runtime activity as completed.</returns>
    public IDisposable SetBusyActivity(LucideIconKind icon, IDynamicLocaleKey headerKey) =>
        Presentation.SetBusyActivity(icon, headerKey);

    public IObservable<IChangeSet<ChatMessageNode>> Connect(Func<ChatMessageNode, bool>? predicate = null) =>
        _branchNodesSourceList.Connect(predicate);

    /// <summary>
    /// Connects to the currently selected chat branch while excluding the internal root prompt
    /// node. Unlike <see cref="DisplayItems"/>, this preserves DynamicData change-set semantics for
    /// presentation projections that must update incrementally. The observable preserves the
    /// source notification thread; each consumer is responsible for choosing its own scheduler.
    /// </summary>
    public IObservable<IChangeSet<ChatMessageNode>> ConnectDisplayItems() =>
        _branchNodesSourceList.Connect(node => node != _rootNode);

    public IObservable<IChangeSet<ChatMessageNode>> Preview(Func<ChatMessageNode, bool>? predicate = null) =>
        _branchNodesSourceList.Preview(predicate);

    /// <summary>
    /// Used for get _branchNodesSourceList items in a no-copy way. Don't use this method to modify the list.
    /// </summary>
    /// <param name="updateAction"></param>
    public void Read(Action<IExtendedList<ChatMessageNode>> updateAction)
    {
        _branchNodesSourceList.Edit(updateAction);
    }

    /// <summary>
    /// Used for get _branchNodesSourceList items in a no-copy way. Don't use this method to modify the list.
    /// </summary>
    /// <param name="updateAction"></param>
    public T Read<T>(Func<IExtendedList<ChatMessageNode>, T> updateAction)
    {
        T? result = default;
        _branchNodesSourceList.Edit(list => result = updateAction(list));
        return result!;
    }

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
    private void UpdateBranchAfterNode(ChatMessageNode node)
    {
        using var _ = _graphMutationLock.EnterScope();

        _branchNodesSourceList.Edit(list =>
        {
            var index = list.IndexOf(node);
            if (index == -1)
                throw new ArgumentOutOfRangeException(nameof(node), "Node is not in the branch nodes.");

            UpdateBranchAfterCore(list, index, node);
        });
    }

    private void UpdateBranchAfter(int index, ChatMessageNode node)
    {
        using var _ = _graphMutationLock.EnterScope();

        UpdateBranchAfterCore(index, node);
    }

    private void UpdateBranchAfterCore(int index, ChatMessageNode node)
    {
        if (index == -1)
            throw new ArgumentOutOfRangeException(nameof(index), "Node is not in the branch nodes.");

        _branchNodesSourceList.Edit(list => UpdateBranchAfterCore(list, index, node));
    }

    private void UpdateBranchAfterCore(IExtendedList<ChatMessageNode> list, int index, ChatMessageNode node)
    {
        for (var i = list.Count - 1; i > index; i--) list.RemoveAt(i);

        // Follow ChoiceIndex down the tree.
        while (true)
        {
            var children = node.Children;
            var choiceIndex = node.ChoiceIndex;
            if (choiceIndex < 0 || choiceIndex >= children.Count) break;
            list.Add(node = _messageNodeMap[children[choiceIndex]]);
        }
    }

    public void Dispose()
    {
        Debug.Assert(!_isDisposed);
        if (_isDisposed)
        {
            Log.ForContext<ChatContext>().Error("ChatContext is already disposed.");
            return;
        }

        _isDisposed = true;

        // The presentation subscribes to the branch and must be torn down before the branch source
        // itself. Clear the field under its dedicated lock so no concurrent lazy getter can publish
        // a new companion after disposal has started.
        ChatPresentation? presentation;
        using (_presentationLock.EnterScope())
        {
            presentation = _presentation;
            _presentation = null;
        }
        presentation?.Dispose();

        using (_graphMutationLock.EnterScope())
        {
            foreach (var node in _messageNodeMap.Values)
            {
                node.PropertyChanged -= HandleNodePropertyChanged;
                node.Dispose();
            }

            _rootNode.PropertyChanged -= HandleNodePropertyChanged;
            _rootNode.Dispose();

            _disposables.Dispose();
            _branchNodesSourceList.Dispose();
        }

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

    /// <summary>
    /// Get and ensures the working directory
    /// </summary>
    /// <returns>
    /// Usually a temporary directory path like C:\Users\[UserName]\AppData\Roaming\Everywhere\plugins\2025-12-30
    /// </returns>
    public string EnsureWorkingDirectory() =>
        RuntimeConstants.EnsureWritableDataFolderPath("plugins", Metadata.DateCreated.ToString("yyyy-MM-dd"));

    public readonly record struct PersistenceNodeSnapshot(
        Guid Id,
        Guid? ParentId,
        Guid? ChoiceChildId,
        ChatMessage Message,
        DateTimeOffset DateModified
    );

    private sealed class ChatPluginUserInterfaceBroker : IChatPluginUserInterfaceBroker, IDisposable
    {
        public IReadOnlyBindableList<ChatPluginUserInterfaceItem> ChatPluginUserInterfaceItems { get; }

        public IReadOnlyBindableList<ChatPluginTodoItem> TodoItems { get; }

        private readonly SourceList<ChatPluginUserInterfaceItem> _chatPluginUserInterfaceItemsSourceList = new();
        private readonly SourceList<ChatPluginTodoItem> _todoItemsSourceList = new();
        private readonly CompositeDisposable _disposables = new(3);

        public ChatPluginUserInterfaceBroker(ChatContext owner)
        {
            ChatPluginUserInterfaceItems = _chatPluginUserInterfaceItemsSourceList
                .Connect()
                .ObserveOnAvaloniaDispatcher()
                .BindEx(_disposables);
            TodoItems = _todoItemsSourceList
                .Connect()
                .ObserveOnAvaloniaDispatcher()
                .BindEx(_disposables);
            _chatPluginUserInterfaceItemsSourceList.CountChanged
                .ObserveOnAvaloniaDispatcher()
                .Subscribe(count =>
                {
                    // On Avalonia UI thread, safe
                    if (count > 0) owner.Metadata.States |= ChatContextMetadataStates.HasNotification;
                    else owner.Metadata.States &= ~ChatContextMetadataStates.HasNotification;
                }).DisposeWith(_disposables);
        }

        public void SetTodoItems(IReadOnlyList<ChatPluginTodoItem> items)
        {
            _todoItemsSourceList.Edit(list => list.Reset(items));
        }

        public async Task<ConsentDecisionResult> HandleConsentRequestAsync(
            IDynamicLocaleKey headerKey,
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

        public async Task<IReadOnlyList<ChatPluginQuestionAnswer>> HandleAskQuestionAsync(
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

        public void Dispose()
        {
            _chatPluginUserInterfaceItemsSourceList.Dispose();
            _todoItemsSourceList.Dispose();
            _disposables.Dispose();
        }
    }
}