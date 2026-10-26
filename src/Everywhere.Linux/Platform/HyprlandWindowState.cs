using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Everywhere.Interop;
using Everywhere.Linux.Hyprland;

namespace Everywhere.Linux.Platform;

/// <summary>
/// UI-thread state for one owned surface, including compositor placement and input policy.
/// </summary>
public sealed partial class HyprlandWindowState : IDisposable, IWindowPresentation
{
    /// <summary>
    /// Gets the observed position, retaining application placement for anchored palettes.
    /// </summary>
    public PixelPoint Position => IsAnchoredPopup ? _position : _window?.Bounds.Position ?? _position;

    /// <summary>
    /// Anchored palettes follow application placement rather than compositor centering.
    /// </summary>
    public bool IsAnchoredPopup { get; set; }

    /// <summary>
    /// Gets or sets whether compositor focus changes should raise this surface.
    /// </summary>
    public bool IsTopmost
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether application geometry is applied as a floating surface.
    /// </summary>
    public bool IsFloating
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether this surface may receive compositor focus.
    /// </summary>
    public bool CanActivate { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the native input region accepts pointer input.
    /// </summary>
    public bool IsHitTestVisible { get; set; } = true;

    /// <summary>
    /// Gets the compositor address while the mapped surface is observed.
    /// </summary>
    public string? Address => _window?.Address;

    /// <summary>
    /// Gets the compositor window state once the mapped surface has been observed.
    /// </summary>
    public WindowState? CompositorWindowState
    {
        get
        {
            if (_window is null)
            {
                return null;
            }

            return _window.Fullscreen switch
            {
                1 => WindowState.Maximized,
                2 => WindowState.FullScreen,
                _ => WindowState.Normal,
            };
        }
    }

    private readonly IWindowImpl _inner;
    private readonly bool _isWayland;
    private readonly Action<PixelPoint> _positionChanged;
    private readonly Action<WindowState> _windowStateChanged;
    private readonly HyprlandConnection _connection = new();
    private readonly string _tokenPrefix;
    private string _token = string.Empty;
    private int _showGeneration;
    private readonly string _ruleName;
    private HyprlandWindow? _window;
    private PixelPoint _position;
    private PixelRect? _fixedBounds;
    private Size _restoredSize;
    private string? _title;
    private bool _isVisible;
    private bool _hasRestoredTitle;
    private bool _hasInputPolicy;
    private bool _hasRule;
    private (bool CanActivate, bool IsHitTestVisible)? _appliedInputPolicy;
    private nint _xDisplay;

    /// <summary>
    /// Creates compositor state for one native application window.
    /// </summary>
    public HyprlandWindowState(
        IWindowImpl inner,
        bool isWayland,
        int id,
        Action<PixelPoint> positionChanged,
        Action<WindowState> windowStateChanged)
    {
        _inner = inner;
        _isWayland = isWayland;
        _positionChanged = positionChanged;
        _windowStateChanged = windowStateChanged;
        _tokenPrefix = $"Everywhere surface {Environment.ProcessId} {id}";
        _ruleName = $"everywhere_surface_{Environment.ProcessId}_{id}";
    }

    /// <summary>
    /// Sets one full-output overlay's global logical placement before it is mapped.
    /// </summary>
    public void SetOverlayBounds(PixelRect bounds)
    {
        _fixedBounds = bounds;
        _position = bounds.Position;
        IsFloating = true;
        IsTopmost = true;
        _inner.Resize(new Size(bounds.Width, bounds.Height), WindowResizeReason.Application);
        if (_isVisible)
        {
            ApplyRule();
            ApplyPlacement();
        }
    }

    /// <summary>
    /// Updates the visible title without replacing the identity of a pending map.
    /// </summary>
    public void SetTitle(string? title)
    {
        _title = title;
        if (!_isVisible || _hasRestoredTitle)
        {
            _inner.SetTitle(title);
        }
    }

    /// <summary>
    /// Prepares a new mapping and its compositor rule before the native window is shown.
    /// </summary>
    public void BeforeShow(Size restoredSize)
    {
        _restoredSize = restoredSize;
        _isVisible = true;
        _hasRestoredTitle = false;
        _hasInputPolicy = false;
        _window = null;
        // Each map needs its own identity: an earlier surface may still appear in
        // IPC snapshots while its asynchronous Wayland unmap is being consumed.
        // Its initial title must never restore the visible title on the new surface.
        _token = $"{_tokenPrefix} {++_showGeneration}";
        _inner.SetTitle(_token);
        ApplyRule();
    }

    /// <summary>
    /// Retains the last position and clears state tied to the hidden appearance.
    /// </summary>
    public void Hidden()
    {
        _position = Position;
        _isVisible = false;
        _window = null;
    }

    /// <summary>
    /// Reconciles compositor geometry and input policy for the current mapped appearance.
    /// </summary>
    public void Observe(IReadOnlyList<HyprlandWindow> clients)
    {
        if (!_isVisible)
        {
            return;
        }

        var observed = clients.FirstOrDefault(item => item.ProcessId == Environment.ProcessId && item.InitialTitle == _token && item.IsMapped);
        if (observed is null)
        {
            return;
        }

        var previous = Position;
        var previousWindowState = CompositorWindowState;
        var isFirstObservation = _window is null;
        _window = observed;
        if (!_hasRestoredTitle)
        {
            _hasRestoredTitle = true;
            _inner.SetTitle(_title);
        }

        if (isFirstObservation)
        {
            ApplyPlacement();
        }
        else if (IsAnchoredPopup && observed.Bounds.Position != _position)
        {
            Move(_position);
        }

        if (isFirstObservation && IsTopmost)
        {
            Raise();
        }

        if (!_hasInputPolicy)
        {
            _hasInputPolicy = ApplyInputPolicy();
        }

        if (_isWayland && previous != Position)
        {
            _positionChanged(Position);
        }

        if (_isWayland && CompositorWindowState is { } windowState && previousWindowState != windowState)
        {
            _windowStateChanged(windowState);
        }
    }

    /// <summary>
    /// Stores the requested position and moves an observed floating Wayland surface.
    /// </summary>
    public void Move(PixelPoint position)
    {
        _position = position;
        if (_isWayland && _window is not null && IsFloating)
        {
            _connection.ExecuteLua(FormattableString.Invariant(
                $"hl.dispatch(hl.dsp.window.move({{x={position.X},y={position.Y},window='address:{_window.Address}'}}))"));
        }
    }

    /// <summary>
    /// Focuses the observed surface when its activation policy permits it.
    /// </summary>
    public void Activate()
    {
        if (_window is not null && CanActivate)
        {
            _connection.ExecuteLua($"hl.dispatch(hl.dsp.focus({{window='address:{_window.Address}'}}))");
        }
    }

    /// <summary>
    /// Raises the observed surface above other compositor windows.
    /// </summary>
    public void Raise()
    {
        if (_window is not null)
        {
            _connection.ExecuteLua($"hl.dispatch(hl.dsp.window.alter_zorder({{mode='top',window='address:{_window.Address}'}}))");
        }
    }

    /// <summary>
    /// Reapplies compositor focus rules and the native pointer input region.
    /// </summary>
    public void RefreshInputPolicy()
    {
        _hasInputPolicy = false;
        if (!_isVisible)
        {
            return;
        }

        // Keep compositor focus rules coherent with the native pointer region when
        // a passive effect or interactive toolbar changes its input policy.
        if (_appliedInputPolicy != (CanActivate, IsHitTestVisible))
        {
            ApplyRule();
        }

        _hasInputPolicy = ApplyInputPolicy();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_hasRule)
        {
            try
            {
                _connection.ExecuteLua($"if _G.{_ruleName} then _G.{_ruleName}:set_enabled(false); _G.{_ruleName}=nil end");
            }
            catch (Exception exception) when (exception is System.Net.Sockets.SocketException or TimeoutException)
            {
                Serilog.Log.Debug(exception, "The compositor disconnected before owned window rule cleanup.");
            }
        }

        if (_xDisplay != 0)
        {
            XCloseDisplay(_xDisplay);
            _xDisplay = 0;
        }
    }

    /// <inheritdoc />
    public async Task WaitForPlacementAsync(PixelRect bounds)
    {
        var generation = _showGeneration;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (_isVisible && generation == _showGeneration)
        {
            deadline.Token.ThrowIfCancellationRequested();
            var clients = _connection.Read<HyprlandWindow[]>("clients");
            Observe(clients);
            var actual = clients.FirstOrDefault(window => window.InitialTitle == _token && window.ProcessId == Environment.ProcessId);
            if (actual is not null)
            {
                if (actual.Bounds == bounds)
                {
                    return;
                }

                ApplyGeometry(bounds.Position, new Size(bounds.Width, bounds.Height));
            }

            await Task.Delay(16, deadline.Token);
        }
        throw new OperationCanceledException("The popup appearance ended before placement completed.");
    }

    private void ApplyRule()
    {
        if (!_hasRule && !IsFloating && CanActivate && IsHitTestVisible)
        {
            _appliedInputPolicy = (CanActivate, IsHitTestVisible);
            return;
        }

        // Keep owned cloaking instantaneous so a fade cannot enter captured pixels.
        // Hyprland's no_focus also prevents normal pointer targeting. Clickable
        // palettes suppress initial focus, while only passive effects forbid focus.
        var noFocus = CanActivate || IsHitTestVisible ? "false" : "true";
        var noInitialFocus = CanActivate ? "false" : "true";
        var effects = $"float=true,no_anim=true,no_focus={noFocus},no_initial_focus={noInitialFocus}";
        if (IsAnchoredPopup)
        {
            effects += ",rounding=0,border_size=0,no_blur=true,no_shadow=true";
        }

        if (_fixedBounds is { } bounds)
        {
            effects += FormattableString.Invariant(
                $",move={{{bounds.X},{bounds.Y}}},size={{{bounds.Width},{bounds.Height}}},rounding=0,border_size=0,no_blur=true,no_shadow=true,no_anim=true,render_unfocused=true");
        }

        _connection.ExecuteLua($"_G.{_ruleName}=hl.window_rule({{name='{_ruleName}',match={{initial_title='^{_token}$'}},{effects}}})");
        _hasRule = true;
        _appliedInputPolicy = (CanActivate, IsHitTestVisible);
    }

    private void ApplyPlacement()
    {
        if (_window is null)
        {
            return;
        }

        if (_fixedBounds is not { } bounds)
        {
            // Honor Avalonia's initial/restored position and the last observed
            // drag position instead of re-centering Chat on every uncloak.
            if (_isWayland && IsFloating)
            {
                ApplyGeometry(_position, _restoredSize);
            }
            return;
        }

        // Window rule positions are output-relative; explicit compositor moves are global.
        ApplyGeometry(bounds.Position, new Size(bounds.Width, bounds.Height));
        Raise();
    }

    private void ApplyGeometry(PixelPoint position, Size size)
    {
        if (_window is null)
        {
            return;
        }

        // Hyprland centers a floating window when resizing it. Move last so
        // that this centering cannot shift the restored top-left position.
        _connection.ExecuteLua(FormattableString.Invariant($$"""
            local target = 'address:{{_window.Address}}'
            hl.dispatch(hl.dsp.window.resize({x={{size.Width}}, y={{size.Height}}, window=target}))
            hl.dispatch(hl.dsp.window.move({x={{position.X}}, y={{position.Y}}, window=target}))
            """));
        // Observe() began with the provisional map geometry. Publishing that
        // old snapshot now would move Avalonia back after the successful restore.
        _window = _connection.Read<HyprlandWindow[]>("clients")
            .FirstOrDefault(window => window.Address == _window.Address && window.ProcessId == Environment.ProcessId) ?? _window;
    }

    private bool ApplyInputPolicy()
    {
        if (_isWayland)
        {
            return WaylandSurfaceInput.TrySetHitTestVisible(_inner, IsHitTestVisible);
        }

        if (_inner.Handle is not { Handle: not 0 } handle)
        {
            return false;
        }

        if (_xDisplay == 0)
        {
            _xDisplay = XOpenDisplay(0);
        }

        if (_xDisplay == 0)
        {
            return false;
        }

        if (IsHitTestVisible)
        {
            XShapeCombineMask(_xDisplay, handle.Handle, 2, 0, 0, 0, 0);
        }
        else
        {
            XShapeCombineRectangles(_xDisplay, handle.Handle, 2, 0, 0, 0, 0, 0, 0);
        }

        XFlush(_xDisplay);
        return true;
    }

    [LibraryImport("libX11.so.6")]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    private static partial int XFlush(nint display);

    [LibraryImport("libXext.so.6")]
    private static partial void XShapeCombineRectangles(
        nint display,
        nint window,
        int kind,
        int x,
        int y,
        nint rectangles,
        int count,
        int operation,
        int ordering);

    [LibraryImport("libXext.so.6")]
    private static partial void XShapeCombineMask(nint display, nint window, int kind, int x, int y, nint mask, int operation);
}