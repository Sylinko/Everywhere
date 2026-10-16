using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Everywhere.Configuration;
using Everywhere.TextSelection;
using Everywhere.Utilities;

namespace Everywhere.Views;

/// <summary>
/// Projects configured actions incrementally into the real toolbar's content control.
/// No execution handler is connected, so preview clicks cannot send a message.
/// </summary>
public partial class TextSelectionToolbarPreview : UserControl
{
    public TextSelectionSettings Settings { get; }

    private readonly TextSelectionToolbarActionsManager _actionsManager;
    private IDisposable? _subscription;
    private TopLevel? _pointerRoot;
    private Point? _pointerPosition;
    private PixelRect? _menuBounds;
    private double _targetOpacity = double.NaN;

    public TextSelectionToolbarPreview(Settings settings, TextSelectionToolbarActionsManager actionsManager)
    {
        Settings = settings.TextSelection;
        _actionsManager = actionsManager;
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _actionsManager.EnsureBuiltInActions();
        PreviewToolbar.Actions = _actionsManager.Items
            .ToObservableChangeSet()
            .AutoRefresh(action => action.IsEnabled)
            .AutoRefresh(action => action.IsAvailable)
            .Filter(action => action is { IsEnabled: true, IsAvailable: true })
            .BindEx(out _subscription);

        Settings.PropertyChanged += HandleSettingsChanged;
        _pointerRoot = TopLevel.GetTopLevel(this);
        if (_pointerRoot is { } root)
        {
            root.AddHandler(PointerMovedEvent, HandlePointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
            root.PointerExited += HandlePointerExited;
        }

        PreviewToolbar.LayoutUpdated += HandlePreviewLayoutUpdated;
        PreviewToolbar.MenuBoundsChanged += HandleMenuBoundsChanged;
        PreviewToolbar.MenuPointerMoved += HandleMenuPointerMoved;
        UpdateOpacity();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DisposeHelper.DisposeToDefault(ref _subscription);

        Settings.PropertyChanged -= HandleSettingsChanged;
        if (_pointerRoot is { } root)
        {
            root.RemoveHandler(PointerMovedEvent, HandlePointerMoved);
            root.PointerExited -= HandlePointerExited;
        }

        _pointerRoot = null;
        _pointerPosition = null;
        _menuBounds = null;
        PreviewToolbar.LayoutUpdated -= HandlePreviewLayoutUpdated;
        PreviewToolbar.MenuBoundsChanged -= HandleMenuBoundsChanged;
        PreviewToolbar.MenuPointerMoved -= HandleMenuPointerMoved;
        PreviewToolbar.Actions = null;

        base.OnDetachedFromVisualTree(e);
    }

    private void HandleSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TextSelectionSettings.ToolbarOpacity) or nameof(TextSelectionSettings.IsToolbarOpacityAdaptive))
        {
            UpdateOpacity();
        }
    }

    private void HandlePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointerRoot is not { } root) return;

        _pointerPosition = e.GetPosition(root);
        UpdateOpacity();
    }

    private void HandlePointerExited(object? sender, PointerEventArgs e)
    {
        _pointerPosition = null;
        UpdateOpacity();
    }

    private void HandleMenuPointerMoved(PixelPoint position)
    {
        if (_pointerRoot is not { } root) return;

        _pointerPosition = root.PointToClient(position);
        UpdateOpacity();
    }

    private void HandleMenuBoundsChanged(PixelRect? bounds)
    {
        _menuBounds = bounds;
        UpdateOpacity();
    }

    private void HandlePreviewLayoutUpdated(object? sender, EventArgs e) => UpdateOpacity();

    private void UpdateOpacity()
    {
        var factor = 1d;
        if (Settings.IsToolbarOpacityAdaptive && _pointerRoot is { } root && PreviewToolbar.TranslatePoint(default, root) is { } origin)
        {
            var bounds = new Rect(origin, PreviewToolbar.Bounds.Size);
            var menuBounds = _menuBounds is { } menu ? new Rect(root.PointToClient(menu.TopLeft), root.PointToClient(menu.BottomRight)) : (Rect?)null;
            factor = _pointerPosition is { } point ? TextSelectionToolbar.CalculateOpacityFactor(point, bounds, menuBounds) : 0.2d;
        }

        var opacity = Settings.ToolbarOpacity * factor;
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (_targetOpacity == opacity) return;

        _targetOpacity = opacity;
        PreviewToolbar.Opacity = opacity;
    }
}