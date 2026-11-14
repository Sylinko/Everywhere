using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Everywhere.Automation;
using Everywhere.Common;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.Linux.Automation;
using Everywhere.Linux.Desktop;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Linux.Platform;

partial class LinuxScreenSelectionService
{
    /// <summary>
    /// Owns one picker lease, its output-local surfaces and its optional frozen
    /// pixels. All presentation state belongs to the UI thread; Host callbacks
    /// cross that boundary once and discard observations from earlier modes.
    /// </summary>
    private sealed class SelectionSession
    {
        public ScreenSelectionMode Mode
        {
            get;
            private set;
        }

        private readonly ICompositorStateProvider _compositor;
        private readonly IOverlayWindowController _overlaysController;
        private readonly ISilentScreenCapturer _screenCapturer;
        private readonly INativeHelper _nativeHelper;
        private readonly IHostedVisualContext _context;
        private readonly SelectionPurpose _purpose;
        private readonly ScreenSelectionMode[] _allowedModes;
        private readonly CancellationTokenSource _lifetime;
        private readonly TaskCompletionSource<SelectionResult?> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<PickerOverlay> _overlays = [];
        private readonly List<FrozenOutput> _frozen = [];
        private readonly SelectionModeWheel _wheel = new();
        private VisualPickerUpdateWorker? _worker;
        private Task? _initialization;
        private PixelPoint _lastPoint;
        private PixelPoint? _dragStart;
        private PixelRect? _selection;
        private PixelRect _desktopBounds;
        private int _modeVersion;
        private bool _isConfirming;
        private bool _isDisposed;

        public SelectionSession(
            ICompositorStateProvider compositor,
            IOverlayWindowController overlays,
            ISilentScreenCapturer screenCapturer,
            INativeHelper nativeHelper,
            IHostedVisualContext context,
            SelectionPurpose purpose,
            ScreenSelectionMode mode,
            CancellationToken cancellationToken)
        {
            _compositor = compositor;
            _overlaysController = overlays;
            _screenCapturer = screenCapturer;
            _nativeHelper = nativeHelper;
            _context = context;
            _purpose = purpose;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _allowedModes = purpose switch
            {
                SelectionPurpose.Application => [ScreenSelectionMode.Window],
                SelectionPurpose.Screenshot =>
                [
                    ScreenSelectionMode.Screen,
                    ScreenSelectionMode.Window,
                    ScreenSelectionMode.Element,
                    ScreenSelectionMode.Free
                ],
                _ => [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
            };
            Mode = _allowedModes.Contains(mode) ? mode : _allowedModes[0];
        }

        public async Task<SelectionResult?> RunAsync()
        {
            try
            {
                var monitors = await _compositor.GetOutputsAsync(_lifetime.Token);
                if (monitors.Count == 0)
                {
                    throw new NotSupportedException("The compositor has no active desktop outputs.");
                }

                _desktopBounds = monitors.Select(item => item.Bounds).Aggregate((left, right) => left.Union(right));
                _lastPoint = await _compositor.GetGlobalCursorPositionAsync(_lifetime.Token);
                if (_purpose == SelectionPurpose.Screenshot)
                {
                    await FreezeOutputsAsync(monitors);
                }

                CreateOverlays(monitors);
                using var cancellation = _lifetime.Token.Register(() => Dispatcher.UIThread.Post(() => _completion.TrySetCanceled()));
                foreach (var overlay in _overlays)
                {
                    overlay.Show();
                    overlay.MoveToolTip(_lastPoint);
                }

                UpdateCandidate();
                return await _completion.Task;
            }
            finally
            {
                await CloseAsync();
            }
        }

        private async Task FreezeOutputsAsync(IEnumerable<OutputInfo> monitors)
        {
            // Freeze before mapping any selector. Confirmation crops these same
            // pixels, so the result cannot contain its mask, outline or tooltip.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, _lifetime.Token);
            await _overlaysController.WaitForHiddenSurfacesAsync(_lifetime.Token);
            foreach (var monitor in monitors)
            {
                var capture = await _screenCapturer.CaptureOutputAsync(monitor.Name, _lifetime.Token);
                try
                {
                    if (capture.Bounds != monitor.Bounds)
                    {
                        throw new InvalidOperationException("The output geometry changed while freezing the desktop.");
                    }

                    var bitmap = capture.ToAvaloniaBitmap() ?? throw new InvalidDataException("The output capture could not be decoded.");
                    var density = LinuxScreenCapture.OutputDensity(monitor);
                    _frozen.Add(new FrozenOutput(monitor.Bounds, capture, bitmap, density));
                }
                catch
                {
                    capture.Dispose();
                    throw;
                }
            }
        }

        private void CreateOverlays(IEnumerable<OutputInfo> monitors)
        {
            var probe = new Window();
            var remainingScreens = probe.Screens.All.ToList();
            probe.Close();
            foreach (var monitor in monitors)
            {
                // Names are authoritative. Geometry matching supports the X11 fallback.
                var screen = remainingScreens.FirstOrDefault(item => item.DisplayName == monitor.Name) ??
                    remainingScreens
                        .OrderBy(item => Math.Abs(item.Bounds.X - monitor.Bounds.X) + Math.Abs(item.Bounds.Y - monitor.Bounds.Y))
                        .FirstOrDefault();
                if (screen is null)
                {
                    continue;
                }
                remainingScreens.Remove(screen);
                var overlay = new PickerOverlay(
                    _nativeHelper,
                    new OutputCoordinateMap(screen.Bounds, monitor.Bounds),
                    screen.Scaling,
                    _allowedModes,
                    Mode,
                    _purpose == SelectionPurpose.Application,
                    _frozen.FirstOrDefault(item => item.Bounds == monitor.Bounds)?.Bitmap)
                {
                    Title = "Everywhere screen selection " + Environment.ProcessId,
                };
                _overlaysController.AttachOverlayWindow(overlay, OverlayRole.ScreenSelector);
                _overlaysController.SetOverlayBounds(overlay, monitor.Bounds);
                overlay.PointerMoved += (_, args) => MovePointer(overlay, args.GetPosition(overlay));
                overlay.PointerPressed += (_, args) => PointerPressed(overlay, args);
                overlay.PointerReleased += (_, args) => PointerReleased(overlay, args);
                overlay.KeyDown += (_, args) => KeyDown(args);
                overlay.PointerWheelChanged += (_, args) =>
                {
                    var step = _wheel.Update(args.Delta.Y, Environment.TickCount64);
                    if (step != 0)
                    {
                        CycleMode(step);
                    }
                    args.Handled = true;
                };
                overlay.Closed += (_, _) => _completion.TrySetResult(null);
                _overlays.Add(overlay);
            }

            if (_overlays.Count == 0)
            {
                throw new NotSupportedException("No desktop output could be matched to the UI backend.");
            }
        }

        private void PointerPressed(PickerOverlay overlay, PointerPressedEventArgs args)
        {
            var point = args.GetCurrentPoint(overlay);
            if (point.Properties.IsRightButtonPressed)
            {
                _completion.TrySetResult(null);
            }
            else if (point.Properties.IsLeftButtonPressed && Mode == ScreenSelectionMode.Free)
            {
                _dragStart = overlay.ToLogical(point.Position);
                _selection = new PixelRect(_dragStart.Value, default(PixelSize));
                args.Pointer.Capture(overlay);
                RenderSelection();
            }
            args.Handled = true;
        }

        private void PointerReleased(PickerOverlay overlay, PointerReleasedEventArgs args)
        {
            if (args.InitialPressMouseButton != MouseButton.Left)
            {
                return;
            }

            MovePointer(overlay, args.GetPosition(overlay));
            _dragStart = null;
            args.Pointer.Capture(null);
            ConfirmAsync().Detach();
            args.Handled = true;
        }

        private void KeyDown(KeyEventArgs args)
        {
            if (args.Key == Key.Escape)
            {
                _completion.TrySetResult(null);
            }
            else if (args.Key == Key.Enter)
            {
                ConfirmAsync().Detach();
            }
            else if (args.Key == Key.Tab)
            {
                CycleMode(args.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
            }
            else if (args.Key is >= Key.D1 and <= Key.D4)
            {
                ChangeMode((ScreenSelectionMode)(args.Key - Key.D1));
            }
            else if (args.Key is >= Key.NumPad1 and <= Key.NumPad4)
            {
                ChangeMode((ScreenSelectionMode)(args.Key - Key.NumPad1));
            }
            else if (args.Key is >= Key.F1 and <= Key.F4)
            {
                ChangeMode((ScreenSelectionMode)(args.Key - Key.F1));
            }
            args.Handled = true;
        }

        private void CycleMode(int step)
        {
            ChangeMode(_allowedModes[(Array.IndexOf(_allowedModes, Mode) + step + _allowedModes.Length) % _allowedModes.Length]);
        }

        private void ChangeMode(ScreenSelectionMode mode)
        {
            if (!_allowedModes.Contains(mode) || mode == Mode || _isConfirming)
            {
                return;
            }
            Mode = mode;
            _modeVersion++;
            _dragStart = null;
            _selection = null;
            _worker?.InvalidateObservation();
            foreach (var overlay in _overlays)
            {
                overlay.SetMode(mode);
            }

            RenderSelection();
            UpdateCandidate();
        }

        private void MovePointer(PickerOverlay overlay, Point point)
        {
            _lastPoint = overlay.ToLogical(point);
            foreach (var item in _overlays)
            {
                item.MoveToolTip(_lastPoint);
            }

            if (Mode == ScreenSelectionMode.Free)
            {
                if (_dragStart is { } start)
                {
                    _selection = SelectionRectangle(start, _lastPoint).Intersect(_desktopBounds);
                }

                RenderSelection();
            }
            else
            {
                UpdateCandidate();
            }
        }

        private void RenderSelection()
        {
            foreach (var overlay in _overlays)
            {
                overlay.SetSelection(_selection);
            }
        }

        private void UpdateCandidate()
        {
            if (Mode == ScreenSelectionMode.Free || _completion.Task.IsCompleted)
            {
                return;
            }

            if (_worker is not null)
            {
                _worker.Update(_lastPoint, Mode);
            }
            else if (_initialization is null)
            {
                _initialization = InitializeWorkerAsync();
                _initialization.Detach(new AnonymousExceptionHandler((exception, _, _, _) => _completion.TrySetException(exception)));
            }
        }

        private async Task InitializeWorkerAsync()
        {
            var picker = await _context.BeginPickerAsync(_lifetime.Token);
            if (_lifetime.IsCancellationRequested)
            {
                picker.Dispose();
                return;
            }

            _worker = new VisualPickerUpdateWorker(_context, picker);
            _worker.ObservationReceived += ObservationReceived;
            _worker.UpdateFailed += UpdateFailed;
            _worker.Update(_lastPoint, Mode);
        }

        private void ObservationReceived(VisualPickerObservation observation)
        {
            var observedVersion = Volatile.Read(ref _modeVersion);
            Dispatcher.UIThread.Post(() =>
            {
                if (_isDisposed || _completion.Task.IsCompleted || Mode == ScreenSelectionMode.Free || observedVersion != _modeVersion)
                {
                    return;
                }

                _selection = observation.Snapshot?.Bounds;
                foreach (var overlay in _overlays)
                {
                    overlay.SetObservation(observation);
                }

                RenderSelection();
            });
        }

        private void UpdateFailed(Exception exception)
        {
            var observedVersion = Volatile.Read(ref _modeVersion);
            Dispatcher.UIThread.Post(() =>
            {
                if (_isDisposed || _completion.Task.IsCompleted || Mode == ScreenSelectionMode.Free || observedVersion != _modeVersion)
                {
                    return;
                }

                if (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failure))
                {
                    ShowFailure(failure);
                }
                else
                {
                    _completion.TrySetException(exception);
                }
            });
        }

        private void ShowFailure(VisualElementQueryFailureKind failure)
        {
            _selection = null;
            foreach (var overlay in _overlays)
            {
                overlay.SetFailure(failure);
            }

            RenderSelection();
        }

        private async Task ConfirmAsync()
        {
            if (_isConfirming || _completion.Task.IsCompleted)
            {
                return;
            }

            _isConfirming = true;
            try
            {
                if (_initialization is not null)
                {
                    await _initialization;
                }

                if (_purpose == SelectionPurpose.Screenshot)
                {
                    if (Mode != ScreenSelectionMode.Free)
                    {
                        if (_worker is null)
                        {
                            return;
                        }

                        _selection = (await _worker.GetCurrentObservationAsync(_lifetime.Token))?.Snapshot?.Bounds;
                    }

                    if (_selection is not { Width: > 0, Height: > 0 } region)
                    {
                        return;
                    }

                    var bitmap = CropFrozenOutputs(_frozen, region.Intersect(_desktopBounds));
                    if (!_completion.TrySetResult(new SelectionResult(null, bitmap)))
                    {
                        bitmap.Dispose();
                    }
                }
                else if (_worker is not null)
                {
                    var anchor = await _worker.ConfirmAsync(cancellationToken: _lifetime.Token);
                    if (anchor is not null && !_completion.TrySetResult(new SelectionResult(anchor, null)))
                    {
                        anchor.Dispose();
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (Exception exception) when (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failure))
            {
                ShowFailure(failure);
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
            finally
            {
                _isConfirming = false;
            }
        }

        private async Task CloseAsync()
        {
            _isDisposed = true;
            _lifetime.Cancel();
            if (_initialization is not null)
            {
                try
                {
                    await _initialization;
                }
                catch (Exception) when (_lifetime.IsCancellationRequested)
                {
                }
            }

            if (_worker is not null)
            {
                _worker.ObservationReceived -= ObservationReceived;
                _worker.UpdateFailed -= UpdateFailed;
                _worker.Dispose();
            }

            foreach (var overlay in _overlays)
            {
                overlay.Close();
            }
            try
            {
                await _overlaysController.WaitForHiddenSurfacesAsync(CancellationToken.None);
            }
            catch (Exception exception) when (exception is OperationCanceledException or System.Net.Sockets.SocketException or TimeoutException)
            {
                Serilog.Log.Debug(exception, "The compositor did not acknowledge selector cleanup before its deadline.");
            }

            foreach (var output in _frozen)
            {
                output.Dispose();
            }

            _lifetime.Dispose();
        }
    }
}