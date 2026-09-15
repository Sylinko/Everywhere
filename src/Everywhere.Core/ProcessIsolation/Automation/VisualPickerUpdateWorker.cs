using Everywhere.Automation;
using Everywhere.Interop;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Coalesces pointer updates so one Host picker has at most one request in flight and one pending coordinate.</summary>
public sealed class VisualPickerUpdateWorker : IDisposable
{
    /// <summary>Raised after Host returns a current revisioned scalar observation.</summary>
    public event Action<VisualPickerObservation>? ObservationReceived;

    /// <summary>Raised when one update fails and the current confirmable observation is cleared.</summary>
    public event Action<Exception>? UpdateFailed;

    private readonly IHostedVisualContext _context;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly Lock _stateGate = new();
    private readonly RemoteVisualPicker _picker;
    private VisualPickerObservation? _latestObservation;
    private PendingUpdate? _pendingUpdate;
    private Task? _worker;
    private bool _isCompleting;
    private bool _isDisposed;

    /// <summary>Creates a coalescing update pump for one already-created remote picker.</summary>
    public VisualPickerUpdateWorker(IHostedVisualContext context, RemoteVisualPicker picker)
    {
        _context = context;
        _picker = picker;
        _lifetimeToken = _lifetimeCancellation.Token;
    }

    /// <summary>Replaces the pending coordinate and starts processing when no request is currently in flight.</summary>
    public void Update(PixelPoint point, ScreenSelectionMode mode)
    {
        lock (_stateGate)
        {
            if (_isDisposed || _isCompleting) return;
            _pendingUpdate = new PendingUpdate(point, mode);
            _worker ??= Task.Run(ProcessUpdatesAsync, _lifetimeToken);
        }
    }

    /// <summary>Drains the latest pending update and transfers its exact Host candidate into an anchor.</summary>
    public async ValueTask<RemoteVisualAnchor?> ConfirmAsync(
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        var worker = BeginCompletion();
        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            var observation = GetLatestObservation();
            if (observation is null)
            {
                Dispose();
                return null;
            }
            using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeToken);
            var anchor = await _context.ConfirmPickerAsync(_picker, observation, query, linkedCancellation.Token).ConfigureAwait(false);
            Dispose();
            return anchor;
        }
        catch (Exception exception) when (AutomationRpcExceptionMapping.TryGetVisualElementQueryFailureKind(exception, out _))
        {
            lock (_stateGate)
            {
                if (!_isDisposed)
                {
                    _isCompleting = false;
                    _latestObservation = null;
                }
            }

            throw;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Cancels pending updates and schedules release of the Host picker.</summary>
    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _pendingUpdate = null;
        }

        _lifetimeCancellation.Cancel();
        _picker.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private Task BeginCompletion()
    {
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _isCompleting = true;
            return _worker ?? Task.CompletedTask;
        }
    }

    private async Task ProcessUpdatesAsync()
    {
        while (TryTakeUpdate(out var update))
        {
            try
            {
                var observation = await _context.UpdatePickerAsync(
                    _picker,
                    update.Point,
                    update.Mode,
                    _lifetimeToken).ConfigureAwait(false);
                lock (_stateGate)
                {
                    if (_isDisposed) return;
                    _latestObservation = observation;
                }

                ObservationReceived?.Invoke(observation);
            }
            catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                lock (_stateGate)
                {
                    _latestObservation = null;
                }

                UpdateFailed?.Invoke(exception);
            }
        }
    }

    private VisualPickerObservation? GetLatestObservation()
    {
        lock (_stateGate)
        {
            return _latestObservation;
        }
    }

    private bool TryTakeUpdate(out PendingUpdate update)
    {
        lock (_stateGate)
        {
            if (!_isDisposed && _pendingUpdate is { } pendingUpdate)
            {
                update = pendingUpdate;
                _pendingUpdate = null;
                return true;
            }

            _worker = null;
            update = default;
            return false;
        }
    }

    private readonly record struct PendingUpdate(PixelPoint Point, ScreenSelectionMode Mode);
}