using Avalonia;
using Avalonia.Threading;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;
using Point = System.Drawing.Point;

namespace Everywhere.Windows.Interop;

/// <summary>A utility class for picking visual elements from the screen.</summary>
public sealed partial class WindowsScreenSelectionService
{
    private sealed class PickerSession : ScreenSelectionSession
    {
        private static ScreenSelectionMode _previousMode = ScreenSelectionMode.Element;

        public static async Task<RemoteVisualAnchor?> PickAsync(
            IWindowHelper windowHelper,
            INativeHelper nativeHelper,
            IHostedVisualContext visualContext,
            ScreenSelectionMode? initialMode,
            bool isApplicationWindowPicker,
            CancellationToken cancellationToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var remotePicker = await visualContext.BeginPickerAsync(cancellationToken);
            var updateWorker = new VisualPickerUpdateWorker(visualContext, remotePicker);
            try
            {
                var mode = isApplicationWindowPicker ? ScreenSelectionMode.Window : initialMode ?? _previousMode;
                var window = new PickerSession(windowHelper, nativeHelper, updateWorker, mode, isApplicationWindowPicker, cancellationToken);
                window.Show();
                return await window._pickingPromise.Task;
            }
            catch
            {
                updateWorker.Dispose();
                throw;
            }
        }

        private readonly TaskCompletionSource<RemoteVisualAnchor?> _pickingPromise = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly VisualPickerUpdateWorker _updateWorker;
        private readonly bool _isApplicationWindowPicker;
        private RemoteVisualAnchor? _result;
        private bool _isConfirming;

        private PickerSession(
            IWindowHelper windowHelper,
            INativeHelper nativeHelper,
            VisualPickerUpdateWorker updateWorker,
            ScreenSelectionMode initialMode,
            bool isApplicationWindowPicker,
            CancellationToken cancellationToken)
            : base(
                windowHelper,
                nativeHelper,
                isApplicationWindowPicker ?
                    [ScreenSelectionMode.Window] :
                    [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
                initialMode)
        {
            _isApplicationWindowPicker = isApplicationWindowPicker;
            ToolTipWindow.ToolTip.IsApplicationWindowPicker = isApplicationWindowPicker;
            _updateWorker = updateWorker;
            _updateWorker.ObservationReceived += HandleObservationReceived;
            _updateWorker.UpdateFailed += HandleUpdateFailed;
            _cancellationRegistration = cancellationToken.Register(() => Dispatcher.Post(CancelFromToken));
        }

        protected override void PickElement(Point cursorPos) =>
            _updateWorker.Update(new PixelPoint(cursorPos.X, cursorPos.Y), CurrentMode);

        protected override void OnSelectionModeChanged()
        {
            _updateWorker.InvalidateObservation();
            ApplyPickingObservation(null);
        }

        protected override bool OnLeftButtonUp()
        {
            if (_isConfirming) return false;
            _isConfirming = true;
            ConfirmAsync().Detach();
            return false;
        }

        protected override void OnClosed(EventArgs e)
        {
            // Application selection must not replace the user's last ordinary picker mode.
            if (!_isApplicationWindowPicker)
            {
                _previousMode = CurrentMode;
            }

            _cancellationRegistration.Dispose();
            _updateWorker.ObservationReceived -= HandleObservationReceived;
            _updateWorker.UpdateFailed -= HandleUpdateFailed;
            _updateWorker.Dispose();
            base.OnClosed(e);
            _pickingPromise.TrySetResult(_result);
        }

        private async Task ConfirmAsync()
        {
            try
            {
                var result = await _updateWorker.ConfirmAsync();
                if (result is null)
                {
                    await Dispatcher.InvokeAsync(() => _isConfirming = false);
                    return;
                }

                _result = result;
                await Dispatcher.InvokeAsync(Close);
            }
            catch (OperationCanceledException)
            {
                _pickingPromise.TrySetResult(null);
                await Dispatcher.InvokeAsync(Close);
            }
            catch (Exception exception) when (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    _isConfirming = false;
                    ApplyPickingFailure(failureKind);
                });
            }
            catch (Exception exception)
            {
                _pickingPromise.TrySetException(exception);
                await Dispatcher.InvokeAsync(Close);
            }
        }

        private void HandleObservationReceived(VisualPickerObservation observation)
        {
            var modeVersion = SelectionModeVersion;
            Dispatcher.Post(() =>
            {
                if (IsVisible && modeVersion == SelectionModeVersion) ApplyPickingObservation(observation);
            });
        }

        private void HandleUpdateFailed(Exception exception)
        {
            var modeVersion = SelectionModeVersion;
            if (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
            {
                Dispatcher.Post(() =>
                {
                    if (IsVisible && modeVersion == SelectionModeVersion) ApplyPickingFailure(failureKind);
                });
                return;
            }

            _pickingPromise.TrySetException(exception);
            Dispatcher.Post(Close);
        }

        private void CancelFromToken()
        {
            if (!IsVisible) return;
            OnCanceled();
            Close();
        }
    }
}