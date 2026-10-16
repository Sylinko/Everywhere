using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Everywhere.Configuration;
using Everywhere.Utilities;
using TextSelectionToolbarAction = Everywhere.TextSelection.TextSelectionToolbarAction;

namespace Everywhere.Views;

/// <summary>
/// Shared, presentation-only toolbar for the floating window and settings preview.
/// Its host decides whether action clicks have any effect.
/// </summary>
public partial class TextSelectionToolbar : UserControl
{
    public static readonly StyledProperty<IReadOnlyList<TextSelectionToolbarAction>?> ActionsProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, IReadOnlyList<TextSelectionToolbarAction>?>(nameof(Actions));

    public static readonly StyledProperty<int> VisibleActionCountProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, int>(nameof(VisibleActionCount), 5);

    public static readonly StyledProperty<bool> CanExcludeApplicationProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, bool>(nameof(CanExcludeApplication), true);

    /// <summary>
    /// Gets or sets the ordered tools offered by the host.
    /// </summary>
    public IReadOnlyList<TextSelectionToolbarAction>? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }

    /// <summary>
    /// Gets or sets how many tools appear directly; zero puts every tool in the menu.
    /// </summary>
    public int VisibleActionCount
    {
        get => GetValue(VisibleActionCountProperty);
        set => SetValue(VisibleActionCountProperty, value);
    }

    /// <summary>
    /// Gets or sets whether this selection has an application identity that can be excluded.
    /// </summary>
    public bool CanExcludeApplication
    {
        get => GetValue(CanExcludeApplicationProperty);
        set => SetValue(CanExcludeApplicationProperty, value);
    }

    public ObservableCollection<TextSelectionToolbarAction> VisibleActions { get; } = [];

    public ObservableCollection<TextSelectionToolbarAction> OverflowActions { get; } = [];

    public event Action<TextSelectionToolbarAction>? ActionInvoked;

    /// <summary>
    /// Requests dismissal of this presentation without changing persisted preferences.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Requests toolbar-only exclusion of the application captured by the host.
    /// </summary>
    public event Action? ExcludeApplicationRequested;

    /// <summary>
    /// Reports the menu's physical bounds so the native dismissal watcher treats it as part of the toolbar.
    /// </summary>
    public event Action<PixelRect?>? MenuBoundsChanged;

    /// <summary>
    /// Reports local menu pointer movement for the settings preview, without requiring global input hooks.
    /// </summary>
    public event Action<PixelPoint>? MenuPointerMoved;

    private Control? _menuPresenter;
    private IDisposable? _menuOpacityBinding;
    private PixelRect? _menuBounds;
    private INotifyCollectionChanged? _observedActions;
    private bool _isAttached;

    public TextSelectionToolbar()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Returns the pointer-distance opacity factor, from 0.2 to 1 in steps of 0.05.
    /// Coordinates are device-independent; the menu counts as part of the toolbar.
    /// </summary>
    public static double CalculateOpacityFactor(Point pointer, Rect toolbarBounds, Rect? menuBounds = null)
    {
        var distance = DistanceTo(pointer, toolbarBounds);
        if (menuBounds is { } menu) distance = Math.Min(distance, DistanceTo(pointer, menu));

        var progress = Math.Clamp((distance - 24d) / (300d - 24d), 0d, 1d);
        var smoothed = progress * progress * (3d - 2d * progress);
        var factor = 1d - 0.8d * smoothed;
        return Math.Clamp((int)Math.Round(factor * 20d), 4, 20) / 20d;

        static double DistanceTo(Point point, Rect bounds)
        {
            var deltaX = Math.Max(Math.Max(bounds.Left - point.X, point.X - bounds.Right), 0d);
            var deltaY = Math.Max(Math.Max(bounds.Top - point.Y, point.Y - bounds.Bottom), 0d);
            return Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        ObserveActions();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CloseMenu();
        _isAttached = false;
        if (_observedActions is not null) _observedActions.CollectionChanged -= HandleActionsChanged;
        _observedActions = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActionsProperty)
        {
            ObserveActions();
        }
        else if (change.Property == VisibleActionCountProperty)
        {
            UpdateActions();
        }
    }

    private void ObserveActions()
    {
        if (_observedActions is not null) _observedActions.CollectionChanged -= HandleActionsChanged;
        _observedActions = _isAttached ? Actions as INotifyCollectionChanged : null;
        if (_observedActions is not null) _observedActions.CollectionChanged += HandleActionsChanged;
        UpdateActions();
    }

    private void HandleActionsChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateActions();

    private void UpdateActions()
    {
        var actions = Actions ?? [];
        var count = Math.Clamp(VisibleActionCount, 0, TextSelectionSettings.MaxVisibleActionCount);
        Reconcile(VisibleActions, actions.Take(count));
        Reconcile(OverflowActions, actions.Skip(count));
    }

    private static void Reconcile(ObservableCollection<TextSelectionToolbarAction> destination, IEnumerable<TextSelectionToolbarAction> source)
    {
        // Preserve unchanged buttons and their hover state when settings reorder or edit another action.
        var index = 0;
        foreach (var action in source)
        {
            if (index < destination.Count && ReferenceEquals(destination[index], action))
            {
                index++;
                continue;
            }

            var existingIndex = destination.IndexOf(action);
            if (existingIndex >= 0)
            {
                destination.Move(existingIndex, index);
            }
            else
            {
                destination.Insert(index, action);
            }
            index++;
        }

        while (destination.Count > index)
        {
            destination.RemoveAt(destination.Count - 1);
        }
    }

    /// <summary>
    /// Closes the menu before its host is hidden or reused for another selection.
    /// </summary>
    public void CloseMenu() => MoreButton.Flyout?.Hide();

    private void HandleMenuOpened(object? sender, EventArgs e)
    {
        _menuPresenter = (sender as MenuFlyout)?.Popup.Child;
        if (_menuPresenter is not null)
        {
            // The flyout caches its presenter; keep this subscription scoped to the open menu.
            _menuOpacityBinding = _menuPresenter.Bind(OpacityProperty, this.GetObservable(OpacityProperty));
            _menuPresenter.LayoutUpdated += HandleMenuLayoutUpdated;
            _menuPresenter.PointerMoved += HandleMenuPointerMoved;
            UpdateMenuBounds();
        }
    }

    private void HandleMenuClosed(object? sender, EventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _menuOpacityBinding);
        if (_menuPresenter is not null)
        {
            _menuPresenter.LayoutUpdated -= HandleMenuLayoutUpdated;
            _menuPresenter.PointerMoved -= HandleMenuPointerMoved;
            _menuPresenter = null;
        }

        _menuBounds = null;
        MenuBoundsChanged?.Invoke(null);
    }

    private void HandleMenuLayoutUpdated(object? sender, EventArgs e) => UpdateMenuBounds();

    private void HandleMenuPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_menuPresenter is { } presenter) MenuPointerMoved?.Invoke(presenter.PointToScreen(e.GetPosition(presenter)));
    }

    private void UpdateMenuBounds()
    {
        if (_menuPresenter is not { } presenter || TopLevel.GetTopLevel(presenter) is null)
        {
            return;
        }

        var bounds = new PixelRect(
            presenter.PointToScreen(default),
            presenter.PointToScreen(new Point(presenter.Bounds.Width, presenter.Bounds.Height)));
        if (_menuBounds != bounds)
        {
            _menuBounds = bounds;
            MenuBoundsChanged?.Invoke(bounds);
        }
    }

    private void HandleCloseClick(object? sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void HandleExcludeApplicationClick(object? sender, RoutedEventArgs e) => ExcludeApplicationRequested?.Invoke();

    private void HandleActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TextSelectionToolbarAction item })
        {
            ActionInvoked?.Invoke(item);
        }
    }
}