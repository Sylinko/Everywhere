using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Interop;
using Everywhere.Utilities;
using ImageIO;

namespace Everywhere.Mac.Interop;

partial class VisualElementContext
{
    private sealed class ScreenshotSession : ScreenSelectionSession
    {
        private static ScreenSelectionModes _previousModes = ScreenSelectionModes.Element;

        public static async Task<Bitmap?> TakeAsync(IWindowHelper windowHelper, ScreenSelectionModes? initialMode)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var window = new ScreenshotSession(windowHelper, initialMode ?? _previousModes);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<Bitmap?> _pickingPromise = new();
        private Bitmap? _resultBitmap;

        private readonly DisposeCollector _disposables = new();

        // Free Mode State
        private bool _isDragging;
        private CGPoint _dragStart;
        private PixelRect _dragRect;

        private ScreenshotSession(IWindowHelper windowHelper, ScreenSelectionModes initialMode) :
            base(windowHelper, ScreenSelectionModes.All, initialMode)
        {
            CaptureAndSetBackground();
        }

        private void CaptureAndSetBackground()
        {
            var screens = NSScreen.Screens;
            for (var i = 0; i < screens.Length; i++)
            {
                if (i >= MaskWindows.Length) break;

                var screen = screens[i];
                var maskWindow = MaskWindows[i];

                if (CaptureScreen(screen.Frame) is not { } bitmap) continue;

                maskWindow.SetImage(bitmap);
                _disposables.Add(bitmap);
            }
        }

        protected override void OnCanceled()
        {
            base.OnCanceled();

            _resultBitmap = null;
        }

        protected override void OnClosed(EventArgs e)
        {
            _previousModes = CurrentMode;
            _disposables.Dispose();
            _pickingPromise.TrySetResult(_resultBitmap);
            base.OnClosed(e);
        }

        protected override void OnLeftButtonDown()
        {
            if (CurrentMode != ScreenSelectionModes.Free) return;

            _dragStart = CurrentMouseLocation; // Cocoa coords (bottom-left)
            // But CurrentMouseLocation is updated in OnPointerMoved.
            // ScreenSelectionSession.CurrentMouseLocation is updated via NSEvent.CurrentMouseLocation (Cocoa)

            _isDragging = true;
            _dragRect = new PixelRect(0, 0, 0, 0);

            // However, OnMove logic uses Quartz point.
            // Let's rely on OnMove to convert and update drag logic if we track drag start in Quartz?

            var primaryScreenHeight = NSScreen.Screens[0].Frame.Height;
            var quartzStart = new CGPoint(_dragStart.X, primaryScreenHeight - _dragStart.Y);

            // Update internal state
            _dragStart = quartzStart; // Store as quartz for consistency with OnMove?

            var dragRect = new PixelRect((int)quartzStart.X, (int)quartzStart.Y, 0, 0);
            foreach (var maskWindow in MaskWindows) maskWindow.SetMask(dragRect);
            UpdateToolTipInfo(dragRect);
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
                if (SelectedElement == null) return false;
                captureRect = SelectedElement.BoundingRectangle;
            }

            WindowHelper.SetCloaked(ToolTipWindow, true);
            Dispatcher.UIThread.Invoke(() => { }, DispatcherPriority.Background);
            _resultBitmap = CaptureScreen(captureRect);
            return true;
        }

        protected override void OnMove(CGPoint point)
        {
            if (CurrentMode == ScreenSelectionModes.Free)
            {
                if (_isDragging)
                {
                    // point is Quartz
                    var startX = _dragStart.X;
                    var startY = _dragStart.Y;

                    var minX = Math.Min(startX, point.X);
                    var minY = Math.Min(startY, point.Y);
                    var maxX = Math.Max(startX, point.X);
                    var maxY = Math.Max(startY, point.Y);

                    _dragRect = new PixelRect((int)minX, (int)minY, (int)(maxX - minX), (int)(maxY - minY));

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

                base.OnMove(point);
            }
        }

        private static Bitmap? CaptureScreen(PixelRect rect)
        {
            return CaptureScreen(new CGRect(rect.X, rect.Y, rect.Width, rect.Height));
        }

        private static Bitmap? CaptureScreen(CGRect rect)
        {
            if (rect.IsEmpty) return null;

            // Adjust rect to be within all screens
            var screens = NSScreen.Screens;
            if (screens.Length > 0)
            {
                var primaryHeight = screens[0].Frame.Height;
                var allScreensRect = CGRect.Empty;

                foreach (var screen in screens)
                {
                    var frame = screen.Frame;
                    // Convert Cocoa coordinates (Bottom-Left) to Quartz coordinates (Top-Left)
                    var y = primaryHeight - (frame.Y + frame.Height);
                    var screenRect = new CGRect(frame.X, y, frame.Width, frame.Height);

                    if (allScreensRect.IsEmpty) allScreensRect = screenRect;
                    else allScreensRect = CGRect.Union(allScreensRect, screenRect);
                }

                rect = CGRect.Intersect(rect, allScreensRect);
            }

            if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0) return null;

#pragma warning disable CA1422 // Validate platform compatibility
            // ReSharper disable once MethodIsTooComplex
            using var cgImage = CGImage.ScreenImage(
                0,
                rect,
                CGWindowListOption.All,
                CGWindowImageOption.Default);
#pragma warning restore CA1422

            if (cgImage is null) return null;

            using var data = new NSMutableData();
            using var dest = CGImageDestination.Create(data, "public.png", 1);

            if (dest is null) return null;

            dest.AddImage(cgImage);
            dest.Close();
            return new Bitmap(data.AsStream());
        }
    }
}