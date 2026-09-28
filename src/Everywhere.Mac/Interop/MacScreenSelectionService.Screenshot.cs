using System.Reactive.Disposables;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using ImageIO;

namespace Everywhere.Mac.Interop;

public sealed partial class MacScreenSelectionService
{
    private sealed class ScreenshotSession : ScreenSelectionSession
    {
        private static ScreenSelectionMode _previousMode = ScreenSelectionMode.Element;

        public static async Task<Bitmap?> TakeAsync(
            IWindowHelper windowHelper,
            IHostedVisualContext visualContext,
            ScreenSelectionMode? initialMode,
            CancellationToken cancellationToken)
        {
            // Give time to hide other windows
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var window = new ScreenshotSession(windowHelper, visualContext, initialMode ?? _previousMode, cancellationToken);
            window.Show();
            return await window._pickingPromise.Task;
        }

        private readonly TaskCompletionSource<Bitmap?> _pickingPromise = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IHostedVisualContext _visualContext;
        private readonly CancellationTokenSource _lifetimeCancellation;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private Bitmap? _resultBitmap;
        private VisualPickerUpdateWorker? _updateWorker;
        private CGPoint _lastPointerPosition;
        private bool _isCompleting;

        private readonly CompositeDisposable _disposables = new();

        // Free Mode State
        private bool _isDragging;
        private CGPoint _dragStart;
        private PixelRect _dragRect;

        private ScreenshotSession(
            IWindowHelper windowHelper,
            IHostedVisualContext visualContext,
            ScreenSelectionMode initialMode,
            CancellationToken cancellationToken)
            : base(
                windowHelper,
                [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element, ScreenSelectionMode.Free],
                initialMode)
        {
            _visualContext = visualContext;
            _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationRegistration = cancellationToken.Register(() => Dispatcher.Post(CancelFromToken));
            CaptureAndSetBackground();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            InitializePickerAsync().Detach();
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
            _cancellationRegistration.Dispose();
            _lifetimeCancellation.Cancel();
            DetachWorker();
            _lifetimeCancellation.Dispose();
            _previousMode = CurrentMode;
            _disposables.Dispose();
            _pickingPromise.TrySetResult(_resultBitmap);
            base.OnClosed(e);
        }

        protected override void OnLeftButtonDown()
        {
            if (CurrentMode != ScreenSelectionMode.Free) return;

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
            if (CurrentMode == ScreenSelectionMode.Free)
            {
                if (!_isDragging) return false;
                _isDragging = false;
                if (_dragRect.Width <= 0 || _dragRect.Height <= 0) return false;
                CaptureAndClose(_dragRect);
                return false;
            }

            if (_isCompleting || _updateWorker is null) return false;
            _isCompleting = true;
            CompleteSnappedScreenshotAsync().Detach();
            return false;
        }

        protected override void OnMove(CGPoint point)
        {
            _lastPointerPosition = point;
            if (CurrentMode == ScreenSelectionMode.Free)
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
                _isDragging = false;
                _updateWorker?.Update(new PixelPoint((int)point.X, (int)point.Y), CurrentMode);
            }
        }

        protected override void OnSelectionModeChanged()
        {
            _updateWorker?.InvalidateObservation();
            ApplyPickingObservation(null);
            _isCompleting = false;
        }

        private async Task InitializePickerAsync()
        {
            try
            {
                var remotePicker = await _visualContext.BeginPickerAsync(_lifetimeCancellation.Token);
                var updateWorker = new VisualPickerUpdateWorker(_visualContext, remotePicker);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsVisible)
                    {
                        updateWorker.Dispose();
                        return;
                    }

                    _updateWorker = updateWorker;
                    updateWorker.ObservationReceived += HandleObservationReceived;
                    updateWorker.UpdateFailed += HandleUpdateFailed;
                    if (CurrentMode != ScreenSelectionMode.Free)
                    {
                        updateWorker.Update(new PixelPoint((int)_lastPointerPosition.X, (int)_lastPointerPosition.Y), CurrentMode);
                    }
                });
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (IsVisible) ApplyPickingFailure(VisualElementQueryFailureKind.ProviderFailure);
                });
            }
        }

        private async Task CompleteSnappedScreenshotAsync()
        {
            var modeVersion = SelectionModeVersion;
            try
            {
                var observation = await _updateWorker!.GetCurrentObservationAsync(_lifetimeCancellation.Token);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsVisible) return;
                    if (modeVersion != SelectionModeVersion)
                    {
                        _isCompleting = false;
                        return;
                    }
                    if (observation?.Snapshot?.Bounds is not { Width: > 0, Height: > 0 } bounds)
                    {
                        _isCompleting = false;
                        ApplyPickingObservation(observation);
                        return;
                    }

                    CaptureAndClose(bounds);
                });
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsVisible) return;
                    _isCompleting = false;
                    if (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
                        ApplyPickingFailure(failureKind);
                    else
                        ApplyPickingFailure(VisualElementQueryFailureKind.ProviderFailure);
                });
            }
        }

        private void CaptureAndClose(PixelRect captureRect)
        {
            WindowHelper.SetCloaked(ToolTipWindow, true);
            Dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            _resultBitmap = CaptureScreen(captureRect);
            Close();
        }

        private void HandleObservationReceived(VisualPickerObservation observation)
        {
            var modeVersion = SelectionModeVersion;
            Dispatcher.Post(() =>
            {
                if (IsVisible && CurrentMode != ScreenSelectionMode.Free && modeVersion == SelectionModeVersion)
                    ApplyPickingObservation(observation);
            });
        }

        private void HandleUpdateFailed(Exception exception)
        {
            var modeVersion = SelectionModeVersion;
            Dispatcher.Post(() =>
            {
                if (!IsVisible || CurrentMode == ScreenSelectionMode.Free || modeVersion != SelectionModeVersion) return;
                if (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
                    ApplyPickingFailure(failureKind);
                else
                    ApplyPickingFailure(VisualElementQueryFailureKind.ProviderFailure);
            });
        }

        private void DetachWorker()
        {
            if (_updateWorker is not { } worker) return;
            _updateWorker = null;
            worker.ObservationReceived -= HandleObservationReceived;
            worker.UpdateFailed -= HandleUpdateFailed;
            worker.Dispose();
        }

        private void CancelFromToken()
        {
            if (!IsVisible) return;
            OnCanceled();
            Close();
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