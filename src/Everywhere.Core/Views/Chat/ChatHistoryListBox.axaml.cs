using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Selection;
using Avalonia.Input;
using Avalonia.VisualTree;
using Everywhere.Chat;
using Everywhere.Collections;
using ShadUI;

namespace Everywhere.Views;

/// <summary>
/// Displays history sections in one selection model. Only conversation rows are selectable;
/// the active conversation belongs to the manager even when its row is not visible.
/// </summary>
public sealed partial class ChatHistoryListBox : ListBox
{
    /// <summary>
    /// Identifies the manager that supplies history and owns navigation.
    /// </summary>
    public static readonly StyledProperty<IChatContextManager?> ContextManagerProperty =
        AvaloniaProperty.Register<ChatHistoryListBox, IChatContextManager?>(nameof(ContextManager));

    /// <summary>
    /// Gets or sets the source of the visible history and the current conversation.
    /// </summary>
    public IChatContextManager? ContextManager
    {
        get => GetValue(ContextManagerProperty);
        set => SetValue(ContextManagerProperty, value);
    }

    /// <inheritdoc />
    protected override Type StyleKeyOverride => typeof(ListBox);

    internal ChatWindowViewModel? ViewModel => DataContext as ChatWindowViewModel;

    private readonly HistorySelectionModel _historySelection;
    private IChatContextManager? _manager;
    private IDisposable? _sectionSubscription;

    /// <summary>
    /// Creates a history list with separate section and conversation containers.
    /// </summary>
    public ChatHistoryListBox()
    {
        Selection = _historySelection = new HistorySelectionModel(this);
        SelectionChanged += HandleSelectionChanged;
        InitializeComponent();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        BindHistory(ContextManager);
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        BindHistory(null);
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContextManagerProperty && this.IsAttachedToVisualTree()) BindHistory(ContextManager);
    }

    /// <inheritdoc />
    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
    {
        if (item is not ChatHistorySection) return base.NeedsContainerOverride(item, index, out recycleKey);

        recycleKey = typeof(ChatHistorySection);
        return true;
    }

    /// <inheritdoc />
    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
    {
        return item is ChatHistorySection ? new ContentPresenter { Focusable = false } : base.CreateContainerForItemOverride(item, index, recycleKey);
    }

    /// <inheritdoc />
    protected override bool ShouldTriggerSelection(Visual selectable, PointerEventArgs eventArgs)
    {
        if (eventArgs.Properties.PointerUpdateKind is PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased)
        {
            return false;
        }

        return base.ShouldTriggerSelection(selectable, eventArgs);
    }

    private void BindHistory(IChatContextManager? manager)
    {
        if (ReferenceEquals(_manager, manager)) return;
        if (_manager is not null) _manager.PropertyChanged -= HandleManagerPropertyChanged;
        _manager = null;
        _sectionSubscription?.Dispose();
        _sectionSubscription = null;
        ItemsSource = null;
        if (manager is null) return;

        // Each segment is the section heading followed by its live, sorted metadata list.
        // CompositeCollection translates child notifications to offsets in the single ListBox.
        var groups = manager.AllHistory
            .ToObservableChangeSet<IReadOnlyBindableList<ChatHistorySection>, ChatHistorySection>()
            .Transform(static section => new CompositeCollection
            {
                Items = { section, new CompositeCollection { ItemsSource = section.Items } }
            })
            .BindEx(out _sectionSubscription);

        ItemsSource = new CompositeCollection { ItemsSource = groups };
        _manager = manager;
        manager.PropertyChanged += HandleManagerPropertyChanged;
        ReflectCurrent();
    }

    private void HandleManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IChatContextManager.CurrentMetadata)) ReflectCurrent();
    }

    private void ReflectCurrent()
    {
        var current = _manager?.CurrentMetadata;
        if (!ReferenceEquals(_historySelection.SelectedItem, current)) _historySelection.SelectedItem = current;
    }

    private void HandleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, this) || _historySelection.IsChangingRows || _manager is not { } manager) return;

        if (SelectedItem is not ChatMetadata requested)
        {
            ReflectCurrent();
            return;
        }

        if (!ReferenceEquals(requested, manager.CurrentMetadata)) manager.CurrentMetadata = requested;
    }

    private sealed class HistorySelectionModel(ChatHistoryListBox owner) : SelectionModel<object>
    {
        public bool IsChangingRows { get; private set; }

        protected override void OnSourceCollectionChangeStarted()
        {
            IsChangingRows = true;
            base.OnSourceCollectionChangeStarted();
        }

        protected override void OnSourceCollectionChangeFinished()
        {
            try
            {
                // Avalonia must first finish adjusting selection indices. A row reappearing
                // after a refresh or search is a presentation change, never a navigation.
                base.OnSourceCollectionChangeFinished();
                owner.ReflectCurrent();
            }
            finally
            {
                IsChangingRows = false;
            }
        }
    }
}
