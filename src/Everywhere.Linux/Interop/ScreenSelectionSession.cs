using System.Diagnostics;
using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using DynamicData;
using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.Views;

namespace Everywhere.Linux.Interop;

internal abstract class ScreenSelectionSession : ScreenSelectionTransparentWindow
{
    protected IWindowBackend Backend { get; }
    protected ScreenSelectionMaskWindow[] MaskWindows { get; }
    protected ScreenSelectionToolTipWindow ToolTipWindow { get; }

    protected ScreenSelectionMode CurrentMode { get; private set; }

    private readonly IReadOnlyList<ScreenSelectionMode> _allowedModes;

    protected ScreenSelectionSession(
        IWindowBackend backend,
        IReadOnlyList<ScreenSelectionMode> allowedModes,
        ScreenSelectionMode initialMode)
    {
        Debug.Assert(allowedModes.Count > 0);

        Backend = backend;
        _allowedModes = allowedModes;
        CurrentMode = initialMode;
        var allScreens = Screens.All;
        MaskWindows = new ScreenSelectionMaskWindow[allScreens.Count];
        var allScreenBounds = new PixelRect();

        for (var i = 0; i < allScreens.Count; i++)
        {
            var screen = allScreens[i];
            allScreenBounds = allScreenBounds.Union(screen.Bounds);
            var maskWindow = new ScreenSelectionMaskWindow(screen.Bounds);
            MaskWindows[i] = maskWindow;
        }

        SetPlacement(allScreenBounds, out _);
        ToolTipWindow = new ScreenSelectionToolTipWindow(allowedModes, initialMode);
        if (backend is X11WindowBackend x11backend)
        {
            foreach (var maskWindow in MaskWindows)
            {
                x11backend.SetHitTestVisible(maskWindow, false);
                x11backend.SetOverrideRedirect(maskWindow, true);
            }
            x11backend.SetHitTestVisible(ToolTipWindow, false);
            x11backend.SetOverrideRedirect(ToolTipWindow, true);
        }

        // Ensure proper initialization of focus/hit-test state
        // On Linux/X11, we rely on the backend to manage window flags/types
    }

    protected override void OnOpened(EventArgs e)
    {
        Backend.SetPickerWindow(this);
        base.OnOpened(e);

        foreach (var maskWindow in MaskWindows) maskWindow.Show(this);
        ToolTipWindow.Show(this);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);

        e.Handled = true;
        e.Pointer.Capture(null);

        if (point.Properties.IsRightButtonPressed)
        {
            OnCanceled();
            Close();
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            OnLeftButtonDown();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;

        if (OnLeftButtonUp())
        {
            Close();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        OnMouseWheel((int)e.Delta.Y);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                OnCanceled();
                Close();
                break;
            case Key.D1:
            case Key.NumPad1:
            case Key.F1:
                CurrentMode = ScreenSelectionMode.Screen;
                HandlePickModeChanged();
                break;
            case Key.D2:
            case Key.NumPad2:
            case Key.F2:
                CurrentMode = ScreenSelectionMode.Window;
                HandlePickModeChanged();
                break;
            case Key.D3:
            case Key.NumPad3:
            case Key.F3:
                CurrentMode = ScreenSelectionMode.Element;
                HandlePickModeChanged();
                break;
            case Key.D4:
            case Key.NumPad4:
            case Key.F4:
                CurrentMode = ScreenSelectionMode.Free;
                HandlePickModeChanged();
                break;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        HandlePointerMoved();
    }

    private void OnMouseWheel(int delta)
    {
        var newIndex = _allowedModes.IndexOf(CurrentMode) + (delta > 0 ? -1 : 1);
        newIndex = Math.Clamp(newIndex, 0, _allowedModes.Count - 1);
        CurrentMode = _allowedModes[newIndex];
        HandlePickModeChanged();
    }

    private void HandlePickModeChanged()
    {
        HandlePointerMoved();
        Dispatcher.UIThread.Post(
            () => ToolTipWindow.ToolTip.Mode = CurrentMode,
            DispatcherPriority.Background);
    }

    private void HandlePointerMoved()
    {
        var point = Backend.GetPointer();
        OnMove(point);
        SetToolTipWindowPosition(point);
    }

    protected override void OnClosed(EventArgs e)
    {
        OnCloseCleanup();
        Backend.SetPickerWindow(null);
        base.OnClosed(e);
    }

    private void SetToolTipWindowPosition(PixelPoint pointerPoint)
    {
        const int margin = 16;

        var screen = Screens.All.FirstOrDefault(s => s.Bounds.Contains(pointerPoint));
        if (screen == null) return;

        var screenBounds = screen.Bounds;
        var tooltipSize = ToolTipWindow.Bounds.Size * ToolTipWindow.DesktopScaling;

        var x = (double)pointerPoint.X;
        var y = pointerPoint.Y - margin - tooltipSize.Height;

        // Check if there is enough space above the pointer
        if (y < 0d)
        {
            y = pointerPoint.Y + margin; // place below the pointer
        }

        // Check if there is enough space to the right of the pointer
        if (x + tooltipSize.Width > screenBounds.Right)
        {
            x = pointerPoint.X - tooltipSize.Width; // place to the left of the pointer
        }

        ToolTipWindow.Position = new PixelPoint((int)x, (int)y);
    }

    // Abstract/Virtual hooks
    protected virtual void OnCanceled() { }
    protected virtual void OnCloseCleanup() { }
    protected abstract void OnMove(PixelPoint point);
    protected virtual void OnLeftButtonDown() { }
    protected virtual bool OnLeftButtonUp() { return true; }
}