using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Utilities;

namespace Everywhere.Linux.Interop;

public partial class VisualElementContext
{
    private sealed class ScreenshotPicker : ScreenSelectionSession
    {
        private static ScreenSelectionModes _previousModes = ScreenSelectionModes.Element;
        public static async Task<Bitmap?> TakeAsync(
            VisualElementContext context,
            IWindowBackend backend,
            ScreenSelectionModes? initialMode)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var window = new ScreenshotPicker(context, backend, initialMode ?? _previousModes);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<Bitmap?> _pickingPromise = new();
        private readonly VisualElementContext _context;
        private Bitmap? _resultBitmap;

        private readonly DisposeCollector _disposables = new();
        private IVisualElement? _selectedElement;

        // Free Mode State
        private bool _isDragging;
        private PixelPoint _dragStart;
        private PixelRect _dragRect;

        private ScreenshotPicker(VisualElementContext context, IWindowBackend backend, ScreenSelectionModes initialMode) :
            base(backend, ScreenSelectionModes.All, initialMode)
        {
            _context = context;
            backend.SetFocusable(this, true);

            CaptureAndSetBackground();
        }

        private void CaptureAndSetBackground()
        {
            var screens = Screens.All;
            for (var i = 0; i < screens.Count; i++)
            {
                if (i >= MaskWindows.Length) break;

                var screen = screens[i];
                var maskWindow = MaskWindows[i];

                if (CaptureScreen(screen.Bounds) is not { } bitmap) continue;

                maskWindow.SetImage(bitmap);
                _disposables.Add(bitmap);
            }
        }

        protected override void OnCanceled()
        {
            _resultBitmap = null;
        }

        protected override void OnCloseCleanup()
        {
            foreach (var maskWindow in MaskWindows)
            {
                maskWindow.SetImage(null);
            }

            foreach (var resource in _disposables)
            {
                resource.Dispose();
            }
            _disposables.Clear();

            _pickingPromise.TrySetResult(_resultBitmap);
        }

        protected override void OnLeftButtonDown()
        {
            if (CurrentMode != ScreenSelectionModes.Free) return;

            _dragStart = Backend.GetPointer();
            _isDragging = true;
            _dragRect = new PixelRect(_dragStart, new PixelSize(0, 0));

            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(_dragRect);
            UpdateToolTipInfo(_dragRect);
        }

        protected override bool OnLeftButtonUp()
        {
            PixelRect captureRect;

            if (CurrentMode == ScreenSelectionModes.Free)
            {
                if (!_isDragging) return false;
                _isDragging = false;
                captureRect = _dragRect;
                if (captureRect.Width <= 0 || captureRect.Height <= 0) return false;
            }
            else
            {
                if (_selectedElement == null) return false;
                captureRect = _selectedElement.BoundingRectangle;
            }

            Backend.SetCloaked(ToolTipWindow, true);
            foreach (var maskWindow in MaskWindows) Backend.SetCloaked(maskWindow, true);
            
            _resultBitmap = CaptureScreen(captureRect);
            return true;
        }

        protected override void OnMove(PixelPoint point)
        {
            if (CurrentMode == ScreenSelectionModes.Free)
            {
                if (_isDragging)
                {
                    var startX = _dragStart.X;
                    var startY = _dragStart.Y;
                    var px = point.X;
                    var py = point.Y;

                    var minX = Math.Min(startX, px);
                    var minY = Math.Min(startY, py);
                    var maxX = Math.Max(startX, px);
                    var maxY = Math.Max(startY, py);

                    _dragRect = new PixelRect(minX, minY, maxX - minX, maxY - minY);

                    foreach (var maskWindow in MaskWindows) maskWindow.SetMask(_dragRect);
                    UpdateToolTipInfo(_dragRect);
                }
                else
                {
                    foreach (var maskWindow in MaskWindows) maskWindow.SetMask(new PixelRect(0, 0, 0, 0));

                    ToolTipWindow.ToolTip.SizeInfo = null;
                }
            }
            else
            {
                // Reuse element picking logic
                _isDragging = false;

                _selectedElement = _context.ElementFromPoint(point, CurrentMode);

                var maskRect = new PixelRect();
                if (_selectedElement != null)
                {
                    maskRect = _selectedElement.BoundingRectangle;
                }

                if (maskRect.Width < 0 || maskRect.Height < 0)
                {
                    maskRect = new PixelRect();
                }

                foreach (var maskWindow in MaskWindows) maskWindow.SetMask(maskRect);
                ToolTipWindow.ToolTip.Element = _selectedElement;
                UpdateToolTipInfo(maskRect);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            _previousModes = CurrentMode;
        }

        private void UpdateToolTipInfo(PixelRect rect)
        {
            ToolTipWindow.ToolTip.SizeInfo = $"{rect.Width} x {rect.Height}";
        }

        private Bitmap? CaptureScreen(PixelRect rect)
        {
            try
            {
                if (rect.Width <= 0 || rect.Height <= 0)
                {
                    return null;
                }

                // Use the backend's Capture method which handles X11 screenshot via XGetImage
                // The backend will capture from the root window (screen)
                using var pointer = Backend.Capture(null, rect);
                return pointer.ToAvaloniaBitmap();
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}