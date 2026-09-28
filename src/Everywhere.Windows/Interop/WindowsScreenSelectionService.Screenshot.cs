using Windows.Win32;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

public sealed partial class WindowsScreenSelectionService
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
        private Point _lastCursorPosition;
        private bool _isCompleting;

        // Free Mode State
        private bool _isDragging;
        private PixelPoint _dragStart;
        private PixelRect _dragRect;

        private ScreenshotSession(
            IWindowHelper windowHelper,
            IHostedVisualContext visualContext,
            ScreenSelectionMode initialMode,
            CancellationToken cancellationToken
        ) : base(
            windowHelper,
            [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element, ScreenSelectionMode.Free],
            initialMode)
        {
            _visualContext = visualContext;
            _lifetimeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cancellationRegistration = cancellationToken.Register(() => Dispatcher.Post(CancelFromToken));
            // Freeze screen for better screenshot experience
            CaptureAndSetBackground();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            InitializePickerAsync().Detach();
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

                // Capture full screen content. This can be expensive with many high-resolution displays,
                // but it is necessary for the frozen-screen selection effect.
                try
                {
                    using var pointer = GDIScreenCapture.Capture(screen.Bounds);
                    if (pointer is not null)
                    {
                        maskWindow.SetImage(pointer.ToAvaloniaBitmap());
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
            _cancellationRegistration.Dispose();
            _lifetimeCancellation.Cancel();
            DetachWorker();
            _lifetimeCancellation.Dispose();
            base.OnClosed(e);

            _previousMode = CurrentMode;
            _pickingPromise.TrySetResult(_resultBitmap);
        }

        protected override void OnLeftButtonDown()
        {
            // If in Free mode, start dragging
            if (CurrentMode != ScreenSelectionMode.Free) return;

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
            if (CurrentMode == ScreenSelectionMode.Free)
            {
                if (!_isDragging) return false; // Clicked without dragging? Maybe treat as single pixel point or ignore?
                _isDragging = false;
                if (_dragRect.Width <= 0 || _dragRect.Height <= 0) return false; // Too small
                CaptureAndClose(_dragRect);
                return false;
            }

            if (_isCompleting || _updateWorker is null) return false;
            _isCompleting = true;
            CompleteSnappedScreenshotAsync().Detach();
            return false;
        }

        protected override void PickElement(Point cursorPos)
        {
            _lastCursorPosition = cursorPos;
            var pixelPoint = new PixelPoint(cursorPos.X, cursorPos.Y);

            if (CurrentMode == ScreenSelectionMode.Free)
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
                _isDragging = false;
                _updateWorker?.Update(pixelPoint, CurrentMode);
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
                        updateWorker.Update(new PixelPoint(_lastCursorPosition.X, _lastCursorPosition.Y), CurrentMode);
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
            using var resultPointer = GDIScreenCapture.Capture(captureRect);
            _resultBitmap = resultPointer?.ToAvaloniaBitmap();
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

        private void UpdateToolTipInfo(PixelRect rect)
        {
            ToolTipWindow.ToolTip.SizeInfo = $"{rect.Width} x {rect.Height}";
        }
    }
}