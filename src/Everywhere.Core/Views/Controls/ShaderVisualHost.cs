using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Rendering.Composition;

namespace Everywhere.Views;

/// <summary>
/// Hosts a shader below the control's own composition visual. Only layout, visibility and
/// shader inputs cross the UI/render thread boundary; frame ticks never return to the control.
/// </summary>
internal sealed class ShaderVisualHost
{
    public bool IsAttached => _visual is not null;

    private readonly Control _owner;
    private readonly Func<SkiaShaderVisualHandler> _createHandler;
    private readonly Action _sendInitialState;

    private CompositionCustomVisual? _visual;
    private Window? _window;
    private Rect? _viewport;
    private bool _isShown;

    public ShaderVisualHost(Control owner, Func<SkiaShaderVisualHandler> createHandler, Action sendInitialState)
    {
        _owner = owner;
        _createHandler = createHandler;
        _sendInitialState = sendInitialState;
        owner.AttachedToVisualTree += OnAttached;
        owner.DetachedFromVisualTree += OnDetached;
        owner.PropertyChanged += OnOwnerChanged;
        ObserveEffectiveVisibility(owner, OnVisibilityChanged);
        owner.EffectiveViewportChanged += OnViewportChanged;
    }

    public void Send(object message) => _visual?.SendHandlerMessage(message);

    // Avalonia propagates ancestor visibility through this internal event. Its publisher is the
    // control that owns this host, avoiding a chain of subscriptions to longer-lived ancestors.
    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "add_IsEffectivelyVisibleChanged")]
    private static extern void ObserveEffectiveVisibility(Visual owner, EventHandler handler);

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (ElementComposition.GetElementVisual(_owner)?.Compositor is not { } compositor) return;

        _window = TopLevel.GetTopLevel(_owner) as Window;
        if (_window is not null) _window.PropertyChanged += OnWindowChanged;
        _visual = compositor.CreateCustomVisual(_createHandler());
        ElementComposition.SetElementChildVisual(_owner, _visual);
        UpdateSize();
        _sendInitialState();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (_window is not null) _window.PropertyChanged -= OnWindowChanged;
        Send(ShaderVisualMessage.Dispose);
        ElementComposition.SetElementChildVisual(_owner, null);
        _visual = null;
        _window = null;
        _viewport = null;
        _isShown = false;
    }

    private void OnOwnerChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Visual.BoundsProperty) UpdateSize();
        else if (change.Property == Visual.OpacityProperty) UpdateVisibility();
    }

    private void OnWindowChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Window.WindowStateProperty) UpdateVisibility();
    }

    private void OnVisibilityChanged(object? sender, EventArgs e) => UpdateVisibility();

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        _viewport = e.EffectiveViewport;
        UpdateVisibility();
    }

    private void UpdateSize()
    {
        if (_visual is null) return;
        var size = _owner.Bounds.Size;
        _visual.Size = new Vector(size.Width, size.Height);
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (_visual is null) return;
        var bounds = new Rect(_owner.Bounds.Size);
        var isShown = _owner is { IsEffectivelyVisible: true, Opacity: > 0.001 } &&
            _window?.WindowState != WindowState.Minimized && bounds is { Width: > 0, Height: > 0 } &&
            (_viewport is not { } viewport || (viewport is { Width: > 0, Height: > 0 } && viewport.Intersects(bounds)));
        if (isShown == _isShown) return;

        _isShown = isShown;
        Send(isShown ? ShaderVisualMessage.Show : ShaderVisualMessage.Hide);
    }
}

internal enum ShaderVisualMessage
{
    Show,
    Hide,
    Dispose
}
