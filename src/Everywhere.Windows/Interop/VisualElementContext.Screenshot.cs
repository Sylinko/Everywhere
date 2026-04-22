using Windows.Win32;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Utilities;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public partial class VisualElementContext
{
    private sealed class ScreenshotSessionWindow : ScreenSelectionSessionWindow
    {
        private static ScreenSelectionModes _previousMode = ScreenSelectionModes.Element;

        public static async Task<Bitmap?> TakeAsync(IWindowHelper windowHelper, ScreenSelectionModes? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new ScreenshotSessionWindow(windowHelper, initialMode ?? _previousMode);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<Bitmap?> _pickingPromise = new();
        private readonly DisposeCollector _disposables = new();

        private Bitmap? _resultBitmap;

        // Free Mode State
        private bool _isDragging;
        private PixelPoint _dragStart;
        private PixelRect _dragRect;

        private ScreenshotSessionWindow(IWindowHelper windowHelper, ScreenSelectionModes initialMode) :
            base(windowHelper, ScreenSelectionModes.All, initialMode)
        {
            // Freeze screen for better screenshot experience
            CaptureAndSetBackground();
        }

        private void CaptureAndSetBackground()
        {
            // We need to capture each screen and set it to the corresponding MaskWindow
            var screens = Screens.All;

            for (var i = 0; i < screens.Count; i++)
            {
                if (i >= MaskWindows.Length) break;

                var screen = screens[i];
                var maskWindow = MaskWindows[i];

                // Capture full screen content
                // Note: using System.Drawing for capture, then converting to Avalonia Bitmap
                // This might be heavy if many screens or high res, but it is necessary for "freeze" effect.
                try
                {
                    using var pointer = CaptureScreen(screen.Bounds);
                    if (pointer is not null)
                    {
                        maskWindow.SetImage(pointer.ToAvaloniaBitmap());
                        _disposables.Add(pointer);
                    }
                }
                catch
                {
                    // If capture fails, we just don't show the background image (fallback to transparent/dimmed)
                }
            }
        }

        protected override void OnCanceled()
        {
            _resultBitmap = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _disposables.Dispose();

            _previousMode = CurrentMode;
            _pickingPromise.TrySetResult(_resultBitmap);
        }

        protected override void OnLeftButtonDown()
        {
            // If in Free mode, start dragging
            if (CurrentMode != ScreenSelectionModes.Free) return;

            PInvoke.GetCursorPos(out var point);
            _dragStart = new PixelPoint(point.X, point.Y);
            _isDragging = true;
            _dragRect = new PixelRect(_dragStart, new PixelSize(0, 0));

            // Update visuals
            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(_dragRect);
            UpdateToolTipInfo(_dragRect);
        }

        protected override bool OnLeftButtonUp()
        {
            PixelRect captureRect;

            if (CurrentMode == ScreenSelectionModes.Free)
            {
                if (!_isDragging) return false; // Clicked without dragging? Maybe treat as single pixel point or ignore?
                _isDragging = false;
                captureRect = _dragRect;
                if (captureRect.Width <= 0 || captureRect.Height <= 0) return false; // Too small
            }
            else
            {
                // Other modes
                if (PickingElement == null) return false;
                captureRect = PickingElement.BoundingRectangle;
            }

            // Hide ToolTip and capture
            WindowHelper.SetCloaked(ToolTipWindow, true);
            using var resultPointer = CaptureScreen(captureRect);
            _resultBitmap = resultPointer?.ToAvaloniaBitmap();
            return true; // Close
        }

        protected override void UpdatePickingElement(Point cursorPos)
        {
            var pixelPoint = new PixelPoint(cursorPos.X, cursorPos.Y);

            if (CurrentMode == ScreenSelectionModes.Free)
            {
                if (_isDragging)
                {
                    // Update Drag Rect
                    var topLeft = new PixelPoint(Math.Min(_dragStart.X, pixelPoint.X), Math.Min(_dragStart.Y, pixelPoint.Y));
                    var bottomRight = new PixelPoint(Math.Max(_dragStart.X, pixelPoint.X), Math.Max(_dragStart.Y, pixelPoint.Y));
                    _dragRect = new PixelRect(topLeft, bottomRight); // Extension or constructor?
                    // PixelRect constructor takes Point, Size.
                    _dragRect = new PixelRect(topLeft, new PixelSize(bottomRight.X - topLeft.X, bottomRight.Y - topLeft.Y));

                    foreach (var maskWindow in MaskWindows) maskWindow.SetMask(_dragRect);
                    UpdateToolTipInfo(_dragRect);
                }
                else
                {
                    // No mask when just hovering in Free Mode?
                    // Or maybe a crosshair?
                    // The mask window draws an overlay, if we pass empty rect, it might mask everything?
                    // In current implementation `SetMask` excludes the rect from the dark overlay.
                    // If we want "Everything Dark", we set mask to Empty?
                    // `SetMask` impl: `_maskBorder.Clip = ... Exclude(maskRect)`.
                    // If maskRect is empty, it excludes nothing, so full dark.
                    foreach (var maskWindow in MaskWindows) maskWindow.SetMask(new PixelRect(0, 0, 0, 0));

                    ToolTipWindow.ToolTip.SizeInfo = null;
                }
            }
            else
            {
                // Logic from VisualElementPicker (Screen/Window/Element)
                // We can duplicate the logic or we should have pushed it to Base or Helper?
                // Duplicating for now as it accesses _selectedElement which is specific here (we need it for capture).

                // Reset Drag state if we switched modes while dragging (should handle in OnModeChanged but Update is enough)
                _isDragging = false;

                base.UpdatePickingElement(cursorPos);
            }
        }

        private void UpdateToolTipInfo(PixelRect rect)
        {
            ToolTipWindow.ToolTip.SizeInfo = $"{rect.Width} x {rect.Height}";
        }
    }
}