using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive.Disposables.Fluent;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Everywhere.Chat.Permissions;
using Everywhere.Chat.Plugins;
using Everywhere.Collections;
using Everywhere.Common;
using Everywhere.Messages;

namespace Everywhere.Chat;

/// <summary>
/// Owns the interaction promises and UI projections shared by one root and its nested chats.
/// Cancellation completes promises without requiring the UI dispatcher to run.
/// </summary>
public sealed class ChatPluginUserInterfaceBroker : IChatPluginUserInterfaceBroker, IDisposable
{
    /// <inheritdoc />
    public IReadOnlyBindableList<ChatPluginUserInterfaceItem> ChatPluginUserInterfaceItems { get; }

    /// <inheritdoc />
    public IChatPluginTodoItemsList TodoItems { get; }

    private readonly SourceList<ChatPluginUserInterfaceItem> _chatPluginUserInterfaceItemsSourceList = new();
    private readonly CompositeDisposable _disposables = new(3);

    /// <summary>
    /// Creates shared interactions whose lifetime is owned by the root, not by a window.
    /// The callback publishes runtime notification state; UI lists observe it separately.
    /// </summary>
    public ChatPluginUserInterfaceBroker(Action<bool> notificationChanged)
    {
        ChatPluginUserInterfaceItems = _chatPluginUserInterfaceItemsSourceList
            .Connect()
            .ObserveOnAvaloniaDispatcher()
            .BindEx(_disposables);
        TodoItems = new ChatPluginTodoItemsList().DisposeWith(_disposables);
        _chatPluginUserInterfaceItemsSourceList.CountChanged
            .Subscribe(count => notificationChanged(count > 0))
            .DisposeWith(_disposables);
    }

    /// <inheritdoc />
    public async Task<ConsentDecision> HandleConsentRequestAsync(
        IDynamicLocaleKey headerKey,
        ChatPluginDisplayBlock? content,
        RequestConsentRememberMasks rememberMasks,
        IReadOnlyList<RequestConsentCustomOption>? customOptions,
        CancellationToken cancellationToken)
    {
        var item = new ChatPluginUserInterfaceConsentRequestItem(headerKey, content, rememberMasks, customOptions, cancellationToken);
        _chatPluginUserInterfaceItemsSourceList.Add(item);
        WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(item.HeaderKey.ToString()));

        try
        {
            return await item.Task.ConfigureAwait(false);
        }
        finally
        {
            _chatPluginUserInterfaceItemsSourceList.Remove(item);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ChatPluginQuestionAnswer>> HandleAskQuestionAsync(
        IReadOnlyList<ChatPluginQuestion> questions,
        CancellationToken cancellationToken = default)
    {
        var item = new ChatPluginUserInterfaceAskQuestionItem(questions, cancellationToken);
        _chatPluginUserInterfaceItemsSourceList.Add(item);
        WeakReferenceMessenger.Default.Send(new FlashChatWindowMessage(item.Questions.FirstOrDefault()?.Question));

        try
        {
            return await item.Task.ConfigureAwait(false);
        }
        finally
        {
            _chatPluginUserInterfaceItemsSourceList.Remove(item);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _chatPluginUserInterfaceItemsSourceList.Dispose();
        _disposables.Dispose();
    }

    private sealed class ChatPluginTodoItemsList : ObservableObject, IChatPluginTodoItemsList, IList, IDisposable
    {
        public ISourceList<ChatPluginTodoItem> SourceList { get; }
        public int Count => _observableList.Count;
        public int CompletedCount => _observableList.AsValueEnumerable().Count(item => item.Status == ChatPluginTodoStatus.Completed);
        public bool IsSynchronized => false;
        public object SyncRoot => _observableList;
        public bool IsFixedSize => false;
        public bool IsReadOnly => true;

        object? IList.this[int index]
        {
            get => _observableList[index];
            set => throw new InvalidOperationException();
        }

        public ChatPluginTodoItem this[int index] => _observableList[index];

        public event NotifyCollectionChangedEventHandler? CollectionChanged;

        private readonly IReadOnlyBindableList<ChatPluginTodoItem> _observableList;
        private readonly IDisposable _subscription;

        public ChatPluginTodoItemsList()
        {
            SourceList = new SourceList<ChatPluginTodoItem>();
            _observableList = SourceList.Connect().ObserveOnAvaloniaDispatcher().BindEx(out _subscription);
            _observableList.CollectionChanged += HandleObservableListCollectionChanged;
            _observableList.PropertyChanged += HandleObservableListPropertyChanged;
        }

        private void HandleObservableListCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            CollectionChanged?.Invoke(this, e);
            OnPropertyChanged(nameof(CompletedCount));
        }

        private void HandleObservableListPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
        }

        public bool Contains(object? value) => ((IList)_observableList).Contains(value);

        public int IndexOf(object? value) => ((IList)_observableList).IndexOf(value);

        public void CopyTo(Array array, int index) => ((IList)_observableList).CopyTo(array, index);

        public int Add(object? value) => throw new InvalidOperationException();

        public void Clear() => throw new InvalidOperationException();

        public void Insert(int index, object? value) => throw new InvalidOperationException();

        public void Remove(object? value) => throw new InvalidOperationException();

        public void RemoveAt(int index) => throw new InvalidOperationException();

        public IEnumerator<ChatPluginTodoItem> GetEnumerator()
        {
            return _observableList.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable)_observableList).GetEnumerator();
        }

        public void Dispose()
        {
            _subscription.Dispose();
            SourceList.Dispose();
            _observableList.CollectionChanged -= HandleObservableListCollectionChanged;
            _observableList.PropertyChanged -= HandleObservableListPropertyChanged;
        }
    }
}