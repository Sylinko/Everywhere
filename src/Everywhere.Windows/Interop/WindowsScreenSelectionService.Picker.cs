using Avalonia;
using Avalonia.Threading;
using Everywhere.Automation;
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
            IVisualElementBackend visualElementBackend,
            VisualContext context,
            IHostedVisualContext visualContext,
            ScreenSelectionMode? initialMode,
            CancellationToken cancellationToken)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

            var remotePicker = await visualContext.BeginPickerAsync(cancellationToken);
            var pump = new VisualPickerUpdateWorker(visualContext, remotePicker);
            try
            {
                var window = new PickerSession(
                    windowHelper,
                    visualElementBackend,
                    context,
                    pump,
                    initialMode ?? _previousMode,
                    cancellationToken);
                window.Show();
                return await window._pickingPromise.Task;
            }
            catch
            {
                pump.Dispose();
                throw;
            }
        }

        private readonly TaskCompletionSource<RemoteVisualAnchor?> _pickingPromise = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private readonly VisualPickerUpdateWorker _worker;
        private RemoteVisualAnchor? _result;
        private bool _isConfirming;

        private PickerSession(
            IWindowHelper windowHelper,
            IVisualElementBackend visualElementBackend,
            VisualContext context,
            VisualPickerUpdateWorker worker,
            ScreenSelectionMode initialMode,
            CancellationToken cancellationToken)
            : base(
                windowHelper,
                visualElementBackend,
                context,
                [ScreenSelectionMode.Screen, ScreenSelectionMode.Window, ScreenSelectionMode.Element],
                initialMode)
        {
            _worker = worker;
            _worker.ObservationReceived += HandleObservationReceived;
            _worker.UpdateFailed += HandleUpdateFailed;
            _cancellationRegistration = cancellationToken.Register(() => Dispatcher.UIThread.Post(CancelFromToken));
        }

        protected override void PickElement(Point cursorPos) =>
            _worker.Update(new PixelPoint(cursorPos.X, cursorPos.Y), CurrentMode);

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
            _worker.ObservationReceived -= HandleObservationReceived;
            _worker.UpdateFailed -= HandleUpdateFailed;
            _worker.Dispose();
            base.OnClosed(e);
            _pickingPromise.TrySetResult(_result);
        }

        private async Task ConfirmAsync()
        {
            try
            {
                _result = await _worker.ConfirmAsync();
                await Dispatcher.UIThread.InvokeAsync(Close);
            }
            catch (OperationCanceledException)
            {
                _pickingPromise.TrySetResult(null);
                await Dispatcher.UIThread.InvokeAsync(Close);
            }
            catch (Exception exception) when (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _isConfirming = false;
                    ApplyPickingSnapshot(null, failureKind);
                });
            }
            catch (Exception exception)
            {
                _pickingPromise.TrySetException(exception);
                await Dispatcher.UIThread.InvokeAsync(Close);
            }
        }

        private void HandleObservationReceived(VisualPickerObservation observation) =>
            Dispatcher.UIThread.Post(() =>
            {
                if (IsVisible) ApplyPickingSnapshot(observation.Snapshot, observation.FailureKind);
            });

        private void HandleUpdateFailed(Exception exception)
        {
            if (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out var failureKind))
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (IsVisible) ApplyPickingSnapshot(null, failureKind);
                });
                return;
            }

            _pickingPromise.TrySetException(exception);
            Dispatcher.UIThread.Post(Close);
        }

        private void CancelFromToken()
        {
            if (!IsVisible) return;
            OnCanceled();
            Close();
        }
    }
}