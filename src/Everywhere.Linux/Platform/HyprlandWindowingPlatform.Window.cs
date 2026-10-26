using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Input.TextInput;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using Everywhere.Linux.Hyprland;

namespace Everywhere.Linux.Platform;

partial class HyprlandWindowingPlatform
{
    private sealed class WindowImpl : IWindowImpl
    {
        public IWindowImpl Inner
        {
            get;
        }

        private readonly HyprlandWindowState _state;
        private readonly HyprlandWindowingPlatform _owner;
        private readonly bool _isWayland;
        private Action<PixelPoint>? _positionChanged;
        private Action<Size, WindowResizeReason>? _resized;
        private Action<WindowState>? _windowStateChanged;
        private Action? _activated;
        private Action? _deactivated;
        private bool _isActive;
        private bool _isApplicationResize;
        private bool _canResize = true;
        private bool _isChangingVisibility;
        private Size? _hiddenClientSize;

        public WindowImpl(IWindowImpl innerWindow, HyprlandWindowingPlatform owner, int id)
        {
            Inner = innerWindow;
            _owner = owner;
            _isWayland = innerWindow.GetType().Namespace?.StartsWith("Avalonia.Wayland", StringComparison.Ordinal) == true;
            _state = new HyprlandWindowState(
                innerWindow,
                _isWayland,
                id,
                point => _positionChanged?.Invoke(point),
                state => _windowStateChanged?.Invoke(state));
            Inner.PositionChanged = point =>
            {
                if (!_isWayland)
                {
                    _positionChanged?.Invoke(point);
                }
            };
            Inner.Resized = HandleResized;
            Inner.Activated = HandleActivated;
            Inner.Deactivated = HandleDeactivated;
            // Hyprland advertises maximized to suppress CSD even on ordinary
            // floating surfaces. Its IPC state is authoritative for the UI.
            Inner.WindowStateChanged = state =>
            {
                if (!_isWayland)
                {
                    _windowStateChanged?.Invoke(state);
                }
            };
        }

        public object? TryGetFeature(Type featureType)
        {
            // HyperlinkButton and TopLevel.Launcher must use the same desktop
            // route as login and other links launched outside a native window.
            if (featureType == typeof(Everywhere.Interop.IWindowPresentation))
            {
                return _state;
            }

            if (featureType == typeof(Avalonia.Platform.Storage.ILauncher))
            {
                return Everywhere.Interop.BetterBclLauncher.Shared;
            }

            return featureType == typeof(HyprlandWindowState) ? _state : Inner.TryGetFeature(featureType);
        }

        public PixelPoint Position => _isWayland ? _state.Position : Inner.Position;

        public double DesktopScaling => Inner.DesktopScaling;

        public double RenderScaling => Inner.RenderScaling;

        public Size ClientSize => Inner.ClientSize;

        public Size? FrameSize => Inner.FrameSize;

        public Size MaxAutoSizeHint => Inner.MaxAutoSizeHint;

        public IPlatformHandle? Handle => Inner.Handle;

        public IPlatformRenderSurface[] Surfaces => Inner.Surfaces;

        public Compositor Compositor => Inner.Compositor;

        public WindowTransparencyLevel TransparencyLevel => Inner.TransparencyLevel;

        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => Inner.AcrylicCompensationLevels;

        public bool WindowStateGetterIsUsable => _isWayland || Inner.WindowStateGetterIsUsable;

        public bool IsClientAreaExtendedToDecorations => Inner.IsClientAreaExtendedToDecorations;

        public bool NeedsManagedDecorations => Inner.NeedsManagedDecorations;
        // Hyprland owns the shadow outside the window geometry. Avalonia's
        // managed shadow adds invisible client margins to custom ShadWindow
        // layouts and makes capture targets disagree with compositor bounds.
        public PlatformRequestedDrawnDecoration RequestedDrawnDecorations
        {
            get
            {
                var requested = Inner.RequestedDrawnDecorations;
                return _isWayland ? requested & ~PlatformRequestedDrawnDecoration.Shadow : requested;
            }
        }

        public Thickness ExtendedMargins => Inner.ExtendedMargins;

        public Thickness OffScreenMargin => Inner.OffScreenMargin;

        public PlatformAllowedWindowActions AllowedWindowActions => Inner.AllowedWindowActions;

        public WindowState WindowState
        {
            get => _isWayland ? _state.CompositorWindowState ?? WindowState.Normal : Inner.WindowState;
            set => Inner.WindowState = value;
        }

        public Action<PixelPoint>? PositionChanged
        {
            get => _positionChanged;
            set => _positionChanged = value;
        }

        public Action<RawInputEventArgs>? Input
        {
            get => Inner.Input;
            set => Inner.Input = value;
        }

        public Action<Rect>? Paint
        {
            get => Inner.Paint;
            set => Inner.Paint = value;
        }

        public Action<Size, WindowResizeReason>? Resized
        {
            get => _resized;
            set => _resized = value;
        }

        public Action<double>? ScalingChanged
        {
            get => Inner.ScalingChanged;
            set => Inner.ScalingChanged = value;
        }

        public Action<WindowTransparencyLevel>? TransparencyLevelChanged
        {
            get => Inner.TransparencyLevelChanged;
            set => Inner.TransparencyLevelChanged = value;
        }

        public Action? Closed
        {
            get => Inner.Closed;
            set => Inner.Closed = value;
        }

        public Action? LostFocus
        {
            get => Inner.LostFocus;
            set => Inner.LostFocus = value;
        }

        public Action? Activated
        {
            get => _activated;
            set => _activated = value;
        }

        public Action? Deactivated
        {
            get => _deactivated;
            set => _deactivated = value;
        }

        public Action? GotInputWhenDisabled
        {
            get => Inner.GotInputWhenDisabled;
            set => Inner.GotInputWhenDisabled = value;
        }

        public Action? DrawnDecorationsRequestChanged
        {
            get => Inner.DrawnDecorationsRequestChanged;
            set => Inner.DrawnDecorationsRequestChanged = value;
        }

        public Func<WindowCloseReason, bool>? Closing
        {
            get => Inner.Closing;
            set => Inner.Closing = value;
        }

        public Action<WindowState>? WindowStateChanged
        {
            get => _windowStateChanged;
            set => _windowStateChanged = value;
        }

        public Action<bool>? ExtendClientAreaToDecorationsChanged
        {
            get => Inner.ExtendClientAreaToDecorationsChanged;
            set => Inner.ExtendClientAreaToDecorationsChanged = value;
        }

        public Action<PlatformAllowedWindowActions>? AllowedWindowActionsChanged
        {
            get => Inner.AllowedWindowActionsChanged;
            set => Inner.AllowedWindowActionsChanged = value;
        }

        public void Show(bool activate, bool isDialog)
        {
            if (!_state.CanActivate && !_state.IsHitTestVisible)
            {
                _state.SetOverlayBounds(new PixelRect(
                    Position,
                    new PixelSize(
                        (int)Math.Round(ClientSize.Width * DesktopScaling),
                        (int)Math.Round(ClientSize.Height * DesktopScaling))));
            }

            var restoredSize = _hiddenClientSize ?? ClientSize;
            _state.BeforeShow(restoredSize);
            _isChangingVisibility = true;
            try
            {
                Inner.Show(activate, isDialog);
                // A new Wayland surface receives an initial configure before its title
                // and floating rule are applied. That provisional tiled size must not
                // replace the existing Chat geometry or be saved as a user resize.
                if (_isWayland && _state.IsFloating)
                {
                    Inner.Resize(restoredSize, WindowResizeReason.Application);
                }

                // Set the native input region immediately, before the first geometry
                // observation, so a newly mapped effect never intercepts a click.
                _state.RefreshInputPolicy();
                _owner.Track(_state);
            }
            catch
            {
                _state.Hidden();
                Inner.Hide();
                throw;
            }
            finally
            {
                _isChangingVisibility = false;
            }
        }

        public void Hide()
        {
            _isActive = false;
            _owner.Refresh();
            _hiddenClientSize = ClientSize;
            _owner.Untrack(_state);
            _state.Hidden();
            _isChangingVisibility = true;
            try
            {
                Inner.Hide();
            }
            finally
            {
                _isChangingVisibility = false;
            }
        }

        public void Dispose()
        {
            _isActive = false;
            _owner.Untrack(_state);
            _state.Dispose();
            Inner.Dispose();
        }

        public void SetTitle(string? title) => _state.SetTitle(title);

        public void Activate()
        {
            if (_isWayland)
            {
                _state.Activate();
            }
            else
            {
                Inner.Activate();
            }
        }

        public void Move(PixelPoint point)
        {
            _state.Move(point);
            if (!_isWayland)
            {
                Inner.Move(point);
            }
        }

        public void SetTopmost(bool value)
        {
            _state.IsTopmost = value;
            Inner.SetTopmost(value);
        }

        public Point PointToClient(PixelPoint point)
        {
            return _isWayland ? new Point(point.X - Position.X, point.Y - Position.Y) : Inner.PointToClient(point);
        }

        public PixelPoint PointToScreen(Point point)
        {
            return _isWayland ?
                new PixelPoint(Position.X + (int)Math.Round(point.X), Position.Y + (int)Math.Round(point.Y)) :
                Inner.PointToScreen(point);
        }

        public void Resize(Size clientSize, WindowResizeReason reason = WindowResizeReason.Application)
        {
            if (_state.Address is null)
            {
                _hiddenClientSize = clientSize;
            }

            var isPreviousApplicationResize = _isApplicationResize;
            _isApplicationResize = true;
            try
            {
                Inner.Resize(clientSize, reason);
            }
            finally
            {
                _isApplicationResize = isPreviousApplicationResize;
            }
        }

        private void HandleActivated()
        {
            _isActive = true;
            _activated?.Invoke();
            if (!_isWayland)
            {
                return;
            }

            // Hyprland deactivates the IME on text-input leave but retains the
            // protocol's enabled state. Re-entering with enable alone then cannot
            // reactivate it. Reset after queued enter events restores the session.
            Dispatcher.UIThread.Post(() =>
            {
                if (_isActive)
                {
                    Inner.TryGetFeature<ITextInputMethodImpl>()?.Reset();
                }
            }, DispatcherPriority.Input);
        }

        private void HandleDeactivated()
        {
            _isActive = false;
            _deactivated?.Invoke();
        }

        private void HandleResized(Size size, WindowResizeReason reason)
        {
            if (_isChangingVisibility)
            {
                return;
            }

            // The Wayland backend labels every server configure as Layout.
            // A mapped interactive floating window resized by Hyprland must
            // leave SizeToContent, just as it does after a native user resize.
            if (_isWayland && _canResize && !_isApplicationResize && reason == WindowResizeReason.Layout &&
                _state is { IsFloating: true, CanActivate: true, Address: not null } && WindowState == WindowState.Normal)
            {
                reason = WindowResizeReason.User;
            }

            _resized?.Invoke(size, reason);
        }

        public void SetParent(IWindowImpl? parent) => Inner.SetParent(parent is WindowImpl wrapped ? wrapped.Inner : parent);

        public void SetInputRoot(IInputRoot inputRoot) => Inner.SetInputRoot(inputRoot);

        public void SetCursor(ICursorImpl? cursor) => Inner.SetCursor(cursor);

        public IPopupImpl? CreatePopup() => Inner.CreatePopup();

        public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> levels) => Inner.SetTransparencyLevelHint(levels);

        public void SetFrameThemeVariant(PlatformThemeVariant? variant) => Inner.SetFrameThemeVariant(variant);

        public void SetEnabled(bool enable) => Inner.SetEnabled(enable);

        public void SetWindowDecorations(WindowDecorations enabled) => Inner.SetWindowDecorations(enabled);

        public void SetIcon(IWindowIconImpl? icon) => Inner.SetIcon(icon);

        public void ShowTaskbarIcon(bool value) => Inner.ShowTaskbarIcon(value);

        public void CanResize(bool canResize)
        {
            _canResize = canResize;
            Inner.CanResize(canResize);
        }

        public void SetCanMinimize(bool value) => Inner.SetCanMinimize(value);

        public void SetCanMaximize(bool value) => Inner.SetCanMaximize(value);

        public void BeginMoveDrag(PointerPressedEventArgs e) => Inner.BeginMoveDrag(e);

        public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) => Inner.BeginResizeDrag(edge, e);

        public void SetMinMaxSize(Size minSize, Size maxSize) => Inner.SetMinMaxSize(minSize, maxSize);

        public void SetExtendClientAreaToDecorationsHint(bool value) => Inner.SetExtendClientAreaToDecorationsHint(value);

        public void SetExtendClientAreaTitleBarHeightHint(double height) => Inner.SetExtendClientAreaTitleBarHeightHint(height);

        public void SetShadowExtents(Thickness extents) => Inner.SetShadowExtents(extents);
    }
}