using System.Reactive.Disposables;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using CoreAnimation;
using Everywhere.Interop;
using Everywhere.Utilities;
using Everywhere.Views;
using ObjCRuntime;

namespace Everywhere.Mac.Interop;

public sealed class WindowHelper : IWindowHelper
{
    private int OpenedWindowCount
    {
        get;
        set
        {
            value = Math.Max(0, value);
            if (value == field) return;

            // changing activation policy
            if (field == 0)
            {
                // first window opened
                AppDelegate.IsVisibleInDock = true;
            }
            else if (value == 0)
            {
                // last window closed
                AppDelegate.IsVisibleInDock = false;
            }

            field = value;
        }
    }

    private bool IsChatWindowCloaked
    {
        set
        {
            if (value == field) return;
            field = value;
            if (value) OpenedWindowCount--;
            else OpenedWindowCount++;
        }
    }

    public WindowHelper()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            OpenedWindowCount = desktop.Windows.Count;
        }

        Window.WindowOpenedEvent.AddClassHandler<Window>(HandleWindowOpened, handledEventsToo: true);
        Window.WindowClosedEvent.AddClassHandler<Window>(HandleWindowClosed, handledEventsToo: true);
    }

    private void HandleWindowOpened(Window window, RoutedEventArgs args)
    {
        if (window is not ChatWindow) OpenedWindowCount++;

        ApplyNativeBackdrop(window);
    }

    private void HandleWindowClosed(Window window, RoutedEventArgs args)
    {
        if (window is not ChatWindow) OpenedWindowCount--;
    }

    /// <summary>
    /// Sets whether the window can become the key window (i.e., receive keyboard focus).
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <param name="focusable">True to allow focus, false to prevent it.</param>
    public void SetFocusable(Window window, bool focusable)
    {
        // We need to NonactivatingPanel, but only NSPanel supports that.
        // So we cannot implement currently.
    }

    /// <summary>
    /// Sets whether the window is transparent to mouse events.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <param name="visible">True to make it receive mouse events, false to let them pass through.</param>
    public void SetHitTestVisible(Window window, bool visible)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return;

        // This is the direct equivalent of WS_EX_TRANSPARENT on Windows.
        nativeWindow.IgnoresMouseEvents = !visible;

        // Special handling to ensure it remains interactive in full screen mode.
        nativeWindow.CollectionBehavior |=
            NSWindowCollectionBehavior.CanJoinAllSpaces |
            NSWindowCollectionBehavior.FullScreenAuxiliary;
        nativeWindow.CollectionBehavior &=
            ~(NSWindowCollectionBehavior.FullScreenPrimary |
                NSWindowCollectionBehavior.Managed);

        if (window is ScreenSelectionMaskWindow or VisualElementEffectWindow)
        {
            nativeWindow.Level = NSWindowLevel.ScreenSaver + 1;
        }
    }

    /// <summary>
    /// Gets the effective visibility of the window, considering its occlusion state.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <returns>True if the window is truly visible on screen.</returns>
    public bool GetEffectiveVisible(Window window)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return window.IsVisible;

        // NSWindow.IsVisible checks if the window is on-screen.
        // NSWindow.OcclusionState tells us if it's obscured by other windows.
        // A window is effectively visible if it's marked as visible and not fully occluded.
        var isVisible = nativeWindow.IsVisible;
        var isOccluded = (nativeWindow.OcclusionState & NSWindowOcclusionState.Visible) == 0;

        return isVisible && !isOccluded;
    }

    /// <summary>
    /// Hides or shows the window from the user's view without destroying it.
    /// macOS doesn't have a direct "Cloak" concept like DWM.
    /// The closest equivalent is hiding the window and managing its space behavior.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <param name="cloaked">True to hide (cloak), false to show (uncloak).</param>
    public void SetCloaked(Window window, bool cloaked)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return;

        if (window is ChatWindow)
        {
            // For ChatWindow, we might want to ensure it can appear on all spaces and in full screen mode.
            nativeWindow.CollectionBehavior =
                NSWindowCollectionBehavior.CanJoinAllSpaces |
                NSWindowCollectionBehavior.FullScreenAuxiliary;

            // Chat window will not be closed, so hide/show is treated as close/open for counting purposes.
            IsChatWindowCloaked = cloaked;
        }

        if (cloaked)
        {
            // Hide the window and ensure it's not in the window cycle (Cmd+Tab).
            nativeWindow.CollectionBehavior |= NSWindowCollectionBehavior.IgnoresCycle;

            // Animate the hiding to avoid flicker
            NSAnimationContext.BeginGrouping();
            NSAnimationContext.CurrentContext.Duration = 0;
            window.Hide();
            NSAnimationContext.EndGrouping();
        }
        else
        {
            // Show the window, make it the frontmost, and restore its cycle behavior.
            window.Show();
            nativeWindow.CollectionBehavior &= ~NSWindowCollectionBehavior.IgnoresCycle;
            nativeWindow.MakeKeyAndOrderFront(null);

            // Make sure it gets an input focus.
#pragma warning disable CA1422
            NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
#pragma warning restore CA1422
        }
    }

    /// <summary>
    /// Checks if the window has any open modal dialogs.
    /// </summary>
    /// <param name="window">The Avalonia window.</param>
    /// <returns>True if a modal dialog is active for this window.</returns>
    public bool AnyModelDialogOpened(Window window)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return false;

        // NSApplication.SharedApplication.ModalWindow returns the current modal window.
        // We check if that modal window's sheet parent is our window.
        var modalWindow = NSApplication.SharedApplication.ModalWindow;
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        if (modalWindow is not null)
        {
            // If a sheet is presented, its Window is the sheet itself, and SheetParent is the owner.
            if (modalWindow.SheetParent.Equals(nativeWindow))
            {
                return true;
            }
        }

        return false;
    }

    public void RequestUserAttention(Window window)
    {
        NSApplication.SharedApplication.RequestUserAttention(NSRequestUserAttentionType.InformationalRequest);
    }

    public bool BringToForeground(Window window)
    {
        // macOS has no equivalent of the Windows foreground lock: activating the application and ordering
        // the window front is enough.
        NSApplication.SharedApplication.ActivateIgnoringOtherApps(true);
        window.Activate();
        return true;
    }

    public double SetCornerRadius(Window window, double radius)
    {
        return WindowFrameModifier.Attach(window, radius);
    }

    public void InitializeWindow(Window window)
    {
        if (GetNativeWindow(window) is not { } nativeWindow) return;

        ApplyNativeBackdrop(window, nativeWindow);

        if (window is ChatWindow)
        {
            // for ChatWindow, disallow closing
            nativeWindow.StyleMask &= ~NSWindowStyle.Closable;
        }
        else
        {
            // for other windows, disable fullscreen
            nativeWindow.CollectionBehavior |= NSWindowCollectionBehavior.FullScreenNone;
            nativeWindow.CollectionBehavior &= ~(NSWindowCollectionBehavior.FullScreenPrimary | NSWindowCollectionBehavior.FullScreenAuxiliary);
        }
    }

    /// <summary>
    /// Gets the native NSWindow from an Avalonia Window.
    /// </summary>
    private static NSWindow? GetNativeWindow(Window window)
    {
        return window.TryGetPlatformHandle()?.Handle is { } handle ? Runtime.GetNSObject<NSWindow>(handle) : null;
    }

    private static void ApplyNativeBackdrop(Window window)
    {
        if (GetNativeWindow(window) is { } nativeWindow)
        {
            ApplyNativeBackdrop(window, nativeWindow);
        }
    }

    private static void ApplyNativeBackdrop(Window window, NSWindow nativeWindow)
    {
        if (window.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur) return;

        nativeWindow.IsOpaque = false;
        nativeWindow.BackgroundColor = NSColor.Clear;

        if (nativeWindow.ContentView is not { } contentView || FindBackdropView(contentView) is not { } backdropView)
            return;

        // Avalonia.Native uses the deprecated Light material for AcrylicBlur, which ignores the
        // window's DarkAqua appearance. A semantic material inherits the effective appearance while
        // retaining AppKit's normal active/inactive states.
        backdropView.Material = NSVisualEffectMaterial.UnderWindowBackground;
        backdropView.State = NSVisualEffectState.FollowsWindowActiveState;
    }

    private static NSVisualEffectView? FindBackdropView(NSView view)
    {
        if (view is NSVisualEffectView { BlendingMode: NSVisualEffectBlendingMode.BehindWindow } backdropView)
        {
            return backdropView;
        }

        foreach (var subview in view.Subviews)
        {
            if (FindBackdropView(subview) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed class WindowFrameModifier : IDisposable
    {
        private static readonly ConditionalWeakTable<Window, WindowFrameModifier> Modifiers = new();

        private readonly Window _window;
        private readonly NSWindow _nativeWindow;
        private readonly CompositeDisposable _subscriptions = new(6);

        private double _radius;
        private bool _isDisposed;
        private bool _isFullScreen;
        private bool? _frameSuppressed;
        private IDisposable? _cornerRadiusOverride;
        private IDisposable? _borderThicknessOverride;

        private WindowFrameModifier(Window window, NSWindow nativeWindow, double radius)
        {
            _window = window;
            _nativeWindow = nativeWindow;
            _radius = Math.Max(0, radius);
        }

        public static double Attach(Window window, double radius)
        {
            if (GetNativeWindow(window) is not { } nativeWindow)
            {
                return 0;
            }

            if (Modifiers.TryGetValue(window, out var existingFrame))
            {
                if (existingFrame._nativeWindow.Handle == nativeWindow.Handle)
                {
                    existingFrame.SetRadius(radius);
                    return radius;
                }

                existingFrame.Dispose();
            }

            var frame = new WindowFrameModifier(window, nativeWindow, radius);
            frame.Attach();
            Modifiers.Add(window, frame);
            return radius;
        }

        private void Attach()
        {
            _subscriptions.Add(_window.GetObservable(Window.WindowStateProperty).Subscribe(_ => Update()));
            _subscriptions.Add(_window.GetObservable(Visual.IsVisibleProperty).Subscribe(_ => Update()));
            _subscriptions.Add(NSWindow.Notifications.ObserveDidResize(_nativeWindow, (_, _) => Update()));
            _subscriptions.Add(NSWindow.Notifications.ObserveWillClose(_nativeWindow, (_, _) => Dispose()));
            _subscriptions.Add(NSWindow.Notifications.ObserveWillEnterFullScreen(_nativeWindow, (_, _) =>
            {
                _isFullScreen = true;
                Update();
            }));
            _subscriptions.Add(NSWindow.Notifications.ObserveDidExitFullScreen(_nativeWindow, (_, _) =>
            {
                _isFullScreen = false;
                Update();
            }));

            Update();
        }

        private void SetRadius(double radius)
        {
            _radius = Math.Max(0, radius);
            Update();
        }

        private void Update()
        {
            ApplyNativeBackdrop(_window, _nativeWindow);

            var suppressFrame =
                !_window.IsVisible ||
                _nativeWindow.IsMiniaturized ||
                _nativeWindow.IsZoomed ||
                _isFullScreen ||
                (_nativeWindow.StyleMask & NSWindowStyle.FullScreenWindow) != 0 ||
                _window.WindowState is WindowState.Minimized or WindowState.Maximized or WindowState.FullScreen;

            if (_frameSuppressed != suppressFrame)
            {
                _frameSuppressed = suppressFrame;
                if (suppressFrame)
                {
                    // Animation priority creates temporary value frames above the AXAML local
                    // values. Disposing them restores whichever frame values are underneath.
                    _cornerRadiusOverride = _window.SetValue(TemplatedControl.CornerRadiusProperty, default, BindingPriority.Animation);
                    _borderThicknessOverride = _window.SetValue(TemplatedControl.BorderThicknessProperty, default, BindingPriority.Animation);
                }
                else
                {
                    DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
                    DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
                }

                _window.InvalidateVisual();
            }

            var effectiveRadius = suppressFrame ? 0 : _radius;
            if (_nativeWindow.ContentView is { Layer: { } layer })
            {
                // Full-screen transitions can rebuild AppKit's frame view hierarchy, so resolve
                // the content layer on every native state update instead of caching it.
                CATransaction.Begin();
                try
                {
                    CATransaction.DisableActions = true;
                    layer.CornerRadius = (nfloat)effectiveRadius;
                    layer.CornerCurve = CACornerCurve.Continuous;
                    layer.MasksToBounds = effectiveRadius > 0;
                }
                finally
                {
                    CATransaction.Commit();
                }
            }

            _nativeWindow.HasShadow = !suppressFrame;
            _nativeWindow.InvalidateShadow();
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (Modifiers.TryGetValue(_window, out var currentFrame) && ReferenceEquals(currentFrame, this))
            {
                Modifiers.Remove(_window);
            }

            DisposeHelper.DisposeToDefault(ref _cornerRadiusOverride);
            DisposeHelper.DisposeToDefault(ref _borderThicknessOverride);
            _subscriptions.Dispose();
        }
    }
}