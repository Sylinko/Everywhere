using Avalonia;
using Avalonia.Threading;
using Everywhere.Extensions;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Automation;

namespace Everywhere.Mac.Interop;

public sealed partial class MacScreenSelectionService
{
    private sealed class PickerSession : ScreenSelectionSession
    {
        private static ScreenSelectionMode _previousMode = ScreenSelectionMode.Element;

        public static async Task<RemoteVisualAnchor?> PickAsync(
            IWindowHelper windowHelper,
            IHostedVisualContext visualContext,
            ScreenSelectionMode? initialMode,
            CancellationToken cancellationToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var remotePicker = await visualContext.BeginPickerAsync(cancellationToken);
            var updateWorker = new VisualPickerUpdateWorker(visualContext, remotePicker);
            try
            {
                var window = new PickerSession(windowHelper, updateWorker, initialMode ?? _previousMode, cancellationToken);
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
        private RemoteVisualAnchor? _result;
        private bool _isConfirming;

        private PickerSession(
            IWindowHelper windowHelper,
            VisualPickerUpdateWorker updateWorker,
            ScreenSelectionMode initialMode,
            CancellationToken cancellationToken
        ) : base(
            windowHelper,
            [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
            initialMode)
        {
            _updateWorker = updateWorker;
            _updateWorker.ObservationReceived += HandleObservationReceived;
            _updateWorker.UpdateFailed += HandleUpdateFailed;
            _cancellationRegistration = cancellationToken.Register(() => Dispatcher.Post(CancelFromToken));
        }

        protected override void OnMove(CGPoint point) =>
            _updateWorker.Update(new PixelPoint((int)point.X, (int)point.Y), CurrentMode);

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
            _previousMode = CurrentMode;
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