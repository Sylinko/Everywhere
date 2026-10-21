using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Everywhere.AI;
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
    public static readonly StyledProperty<bool> IsFreeformChatEnabledProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, bool>(nameof(IsFreeformChatEnabled));

    public static readonly DirectProperty<TextSelectionToolbar, bool> IsFreeformChatProperty =
        AvaloniaProperty.RegisterDirect<TextSelectionToolbar, bool>(nameof(IsFreeformChat), o => o.IsFreeformChat);

    public static readonly StyledProperty<IReadOnlyList<CustomAssistant>?> CustomAssistantsProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, IReadOnlyList<CustomAssistant>?>(nameof(CustomAssistants));

    public static readonly StyledProperty<CustomAssistant?> SelectedCustomAssistantProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, CustomAssistant?>(nameof(SelectedCustomAssistant));

    public static readonly StyledProperty<IReadOnlyList<TextSelectionToolbarAction>?> ActionsProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, IReadOnlyList<TextSelectionToolbarAction>?>(nameof(Actions));

    public static readonly StyledProperty<int> VisibleActionCountProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, int>(nameof(VisibleActionCount), 5);

    public static readonly StyledProperty<bool> CanExcludeApplicationProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, bool>(nameof(CanExcludeApplication), true);

    public static readonly StyledProperty<string?> FreeformChatTextProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, string?>(nameof(FreeformChatText));

    public static readonly StyledProperty<IRelayCommand<string>?> FreeformChatCommandProperty =
        AvaloniaProperty.Register<TextSelectionToolbar, IRelayCommand<string>?>(nameof(FreeformChatCommand));

    /// <summary>
    /// Gets or sets the draft retained while switching modes within one selection.
    /// </summary>
    public string? FreeformChatText
    {
        get => GetValue(FreeformChatTextProperty);
        set => SetValue(FreeformChatTextProperty, value);
    }

    /// <summary>
    /// Gets or sets the host's submission command. A preview leaves this unset.
    /// The host clears the draft only after accepting it.
    /// </summary>
    public IRelayCommand<string>? FreeformChatCommand
    {
        get => GetValue(FreeformChatCommandProperty);
        set => SetValue(FreeformChatCommandProperty, value);
    }

    /// <summary>
    /// Gets or sets whether the freeform chat feature is enabled.
    /// </summary>
    public bool IsFreeformChatEnabled
    {
        get => GetValue(IsFreeformChatEnabledProperty);
        set => SetValue(IsFreeformChatEnabledProperty, value);
    }

    /// <summary>
    /// Gets whether the toolbar is currently presenting its freeform editor.
    /// </summary>
    public bool IsFreeformChat
    {
        get;
        private set => SetAndRaise(IsFreeformChatProperty, ref field, value);
    }

    /// <summary>
    /// Gets or sets the collection of custom assistants available for selection in the toolbar.
    /// </summary>
    public IReadOnlyList<CustomAssistant>? CustomAssistants
    {
        get => GetValue(CustomAssistantsProperty);
        set => SetValue(CustomAssistantsProperty, value);
    }

    /// <summary>
    /// Gets or sets the currently selected custom assistant in the toolbar.
    /// </summary>
    public CustomAssistant? SelectedCustomAssistant
    {
        get => GetValue(SelectedCustomAssistantProperty);
        set => SetValue(SelectedCustomAssistantProperty, value);
    }

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
    /// Supplies the lazily attached editor to an interactive host. Settings previews leave this unwired.
    /// </summary>
    public event Action<ChatTextEditor>? FreeformChatInputRequested;

    /// <summary>
    /// Releases host input before the editor starts transitioning out of the presentation.
    /// </summary>
    public event Action? FreeformChatExiting;

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

    private PopupFlyoutBase? _menuFlyout;
    private PopupFlyoutBase? _assistantFlyout;
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
        ExitFreeformChat();
        CloseMenu();
        ObserveAssistantFlyout(null);
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
    public void CloseMenu() => _menuFlyout?.Hide();

    /// <summary>
    /// Closes an open menu first, otherwise returns from editing to the tools.
    /// The input host must give an active input-method composition priority over this action.
    /// </summary>
    public void HandleEscape()
    {
        if (_menuFlyout is not null)
        {
            CloseMenu();
            return;
        }

        ExitFreeformChat();
    }

    private void HandleMenuOpened(object? sender, EventArgs e)
    {
        _menuFlyout = sender as PopupFlyoutBase;
        _menuPresenter = _menuFlyout?.Popup.Child;
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

        _menuFlyout = null;
        _menuBounds = null;
        MenuBoundsChanged?.Invoke(null);

        if (IsFreeformChat)
        {
            RequestFreeformChatInput();
        }
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

    private void HandleEnterFreeformChatClick(object? sender, RoutedEventArgs e)
    {
        if (!IsFreeformChatEnabled)
        {
            return;
        }

        CloseMenu();
        IsFreeformChat = true;

        // ConditionalContentControl builds its branch during measure, not on the Boolean change.
        // Complete normal layout before handing the editor to the host. No delay or retry is needed;
        // native preparation already ran when the passive toolbar appeared, in an earlier interaction.
        UpdateLayout();
        RequestFreeformChatInput();
    }

    private void HandleExitFreeformChatClick(object? sender, RoutedEventArgs e)
    {
        ExitFreeformChat();
    }

    /// <summary>
    /// Returns to the tools, releasing delegated input before the outgoing editor animates away.
    /// </summary>
    public void ExitFreeformChat()
    {
        if (!IsFreeformChat)
        {
            return;
        }

        FreeformChatExiting?.Invoke();
        IsFreeformChat = false;
        CloseMenu();
    }

    /// <summary>
    /// Resets a pooled window to its passive presentation without animating a previous editor.
    /// Cached branches remain reusable, but the editor is detached from the visual tree.
    /// </summary>
    public void ResetFreeformChat()
    {
        ExitFreeformChat();
        SetCurrentValue(FreeformChatTextProperty, null);
        ModeContent.UpdateContentWithoutTransition();
    }

    private void RequestFreeformChatInput()
    {
        // Read the current branch rather than the whole toolbar: an outgoing transition presenter
        // may still contain another visual until its animation completes.
        if (IsFreeformChat && ModeContent.Content is ChatInputArea area &&
            area.GetVisualDescendants().OfType<ChatTextEditor>().FirstOrDefault() is { } editor)
        {
            FreeformChatInputRequested?.Invoke(editor);
        }
    }

    private void HandleAssistantSelectorLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is ChatCustomAssistantSelector selector)
        {
            var button = selector.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(button => button.Flyout is PopupFlyoutBase);
            ObserveAssistantFlyout(button?.Flyout as PopupFlyoutBase);
        }
    }

    private void ObserveAssistantFlyout(PopupFlyoutBase? flyout)
    {
        if (ReferenceEquals(_assistantFlyout, flyout))
        {
            return;
        }

        if (_assistantFlyout is { } previous)
        {
            previous.Opened -= HandleMenuOpened;
            previous.Closed -= HandleMenuClosed;
        }

        _assistantFlyout = flyout;
        if (flyout is not null)
        {
            // The popup has its own HWND but belongs to this input surface. Opening it must not
            // replace the hidden CoreWindow's native focus. Logical menu focus remains available.
            flyout.Popup.TakesFocusFromNativeControl = false;
            flyout.Opened += HandleMenuOpened;
            flyout.Closed += HandleMenuClosed;
        }
    }

    private void HandleActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: TextSelectionToolbarAction item })
        {
            ActionInvoked?.Invoke(item);
        }
    }
}