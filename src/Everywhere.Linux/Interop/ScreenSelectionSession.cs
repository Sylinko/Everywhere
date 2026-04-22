using Avalonia;
using Avalonia.Input;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Views;

namespace Everywhere.Linux.Interop;

internal abstract class ScreenSelectionSession : ScreenSelectionSessionWindowBase
{
    protected IWindowBackend Backend { get; }
    protected ScreenSelectionMaskWindow[] MaskWindows { get; }
    protected ScreenSelectionToolTipWindow ToolTipWindow { get; }

    protected ScreenSelectionSession(
        IWindowBackend backend,
        ScreenSelectionModes allowedModes,
        ScreenSelectionModes initialMode) : base(allowedModes, initialMode)
    {
        Backend = backend;

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
        if (backend is X11WindowBackend x11Backend)
        {
            foreach (var maskWindow in MaskWindows)
            {
                x11Backend.SetHitTestVisible(maskWindow, false);
                x11Backend.SetOverrideRedirect(maskWindow, true);
            }
            x11Backend.SetHitTestVisible(ToolTipWindow, false);
            x11Backend.SetOverrideRedirect(ToolTipWindow, true);
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
        base.OnPointerPressed(e);

        e.Handled = true;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        HandlePointerMoved();
    }

    protected override void HandleModeChanged()
    {
        Dispatcher.UIThread.Post(() =>
        {
            HandlePointerMoved();
            ToolTipWindow.ToolTip.CurrentMode = CurrentMode;
        });
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

        Size tooltipSize;
        if (!ToolTipWindow.IsVisible)
        {
            ToolTipWindow.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            tooltipSize = ToolTipWindow.DesiredSize * screen.Scaling;
        }
        else
        {
            tooltipSize = ToolTipWindow.Bounds.Size * screen.Scaling;
        }

        var x = (double)pointerPoint.X;
        var y = pointerPoint.Y - margin - tooltipSize.Height;

        // Check if there is enough space above the pointer
        if (y < 0d)
        {
            y = pointerPoint.Y + margin; // place below the pointer
        }

        // Check if there is enough space to the right of the pointer
        if (x + tooltipSize.Width > screen.Bounds.Right)
        {
            x = pointerPoint.X - tooltipSize.Width; // place to the left of the pointer
        }

        ToolTipWindow.Position = new PixelPoint((int)x, (int)y);
    }

    protected virtual void OnCloseCleanup() { }

    protected abstract void OnMove(PixelPoint point);
}