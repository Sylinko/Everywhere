using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;
using Everywhere.Utilities;

namespace Everywhere.ProcessIsolation.Automation;

public sealed partial class AutomationHostSession
{
    /// <inheritdoc />
    public async ValueTask<RpcAck> BeginPickerAsync(
        BeginVisualPickerRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = GetContext(request.ContextId);
        try
        {
            return await context.ExecuteAsync(
                resource => resource.BeginPicker(request),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _resources.RegisterAsync(request.PickerId, new VisualPickerRelease(context, request.PickerId)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask<VisualPickerObservation> UpdatePickerAsync(
        UpdateVisualPickerRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => ValueTask.FromResult(resource.UpdatePicker(request, token)),
            cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask<AcquireAutomationAnchorResponse> ConfirmPickerAsync(
        ConfirmVisualPickerRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = GetContext(request.ContextId);
        try
        {
            return await context.ExecuteAsync(
                (resource, token) => ValueTask.FromResult(resource.ConfirmPicker(request, token)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _resources.RegisterAsync(request.AnchorId, new AutomationAnchorRelease(context, request.AnchorId)).ConfigureAwait(false);
        }
    }

    private sealed class VisualPickerRelease(AutomationContextResource context, long pickerId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => context.ReleasePickerAsync(pickerId);
    }

    private sealed partial class AutomationContextResource
    {
        private static readonly VisualElementQueryRequest PickerQuery = new(
            VisualElementFields.Type | VisualElementFields.Bounds | VisualElementFields.ProcessId,
            0);

        private readonly Dictionary<long, VisualPicker> _pickers = [];

        public RpcAck BeginPicker(BeginVisualPickerRequest request)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.PickerId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MainProcessId);
            if (!_pickers.TryAdd(request.PickerId, new VisualPicker(request.MainProcessId)))
            {
                throw new InvalidOperationException($"Visual picker {request.PickerId} is already registered in this Context.");
            }

            return default;
        }

        public VisualPickerObservation UpdatePicker(UpdateVisualPickerRequest request, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Revision);
            cancellationToken.ThrowIfCancellationRequested();
            var picker = GetPicker(request.PickerId);
            if (request.Revision <= picker.Revision)
            {
                throw new InvalidOperationException(
                    $"Visual picker revision {request.Revision} must be greater than its current revision {picker.Revision}.");
            }

            var retention = Context.CreateRetention();
            var isRetentionOwnedByOperation = true;
            try
            {
                var result = _pickerResolver.Resolve(
                    _backend,
                    retention,
                    new PixelPoint(request.PointX, request.PointY),
                    request.Mode,
                    picker.ExcludedProcessId,
                    PickerQuery);
                cancellationToken.ThrowIfCancellationRequested();
                if (result is null)
                {
                    retention.Dispose();
                    picker.Replace(request.Revision, null, null);
                }
                else
                {
                    picker.Replace(request.Revision, retention, result);
                    isRetentionOwnedByOperation = false;
                }

                return new VisualPickerObservation
                {
                    Revision = request.Revision,
                    Candidate = result.ToResponse(),
                };
            }
            catch
            {
                if (isRetentionOwnedByOperation) retention.Dispose();
                picker.Replace(request.Revision, null, null);
                throw;
            }
        }

        public AcquireAutomationAnchorResponse ConfirmPicker(
            ConfirmVisualPickerRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.AnchorId);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.Revision);
            if (_anchors.ContainsKey(request.AnchorId))
            {
                throw new InvalidOperationException($"Automation anchor {request.AnchorId} is already registered in this Context.");
            }

            var picker = GetPicker(request.PickerId);
            if (request.Revision != picker.Revision)
            {
                throw new InvalidOperationException(
                    $"Visual picker revision {request.Revision} does not match its current revision {picker.Revision}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var anchor = picker.TakeAnchor(new VisualElementQueryRequest(request.RequestedFields, request.MaxTextCharacters));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _pickers.Remove(request.PickerId);
                picker.Dispose();
                if (anchor is null) return ((VisualElementQueryResult?)null).ToResponse();

                var response = anchor.Result.ToResponse();
                _anchors.Add(request.AnchorId, anchor);
                return response;
            }
            catch
            {
                anchor?.Retention.Dispose();
                throw;
            }
        }

        public ValueTask ReleasePickerAsync(long pickerId)
        {
            var workItem = new ContextWorkItem<RpcAck>(
                (resource, _) =>
                {
                    if (resource._pickers.Remove(pickerId, out var picker)) picker.Dispose();
                    return ValueTask.FromResult(default(RpcAck));
                },
                CancellationToken.None);
            lock (_queueGate)
            {
                if (_isDisposing) return ValueTask.CompletedTask;
                if (!_operations.Writer.TryWrite(workItem)) throw new InvalidOperationException("The Automation Context queue is closed.");
            }

            return new ValueTask(workItem.Completion);
        }

        private VisualPicker GetPicker(long pickerId) => _pickers.TryGetValue(pickerId, out var picker) ?
            picker :
            throw new InvalidOperationException($"Visual picker {pickerId} is no longer available.");

        private sealed class VisualPicker(int excludedProcessId) : IDisposable
        {
            public int ExcludedProcessId { get; } = excludedProcessId;

            public long Revision { get; private set; }

            private VisualElementQueryResult? _result;
            private VisualElementRetention? _retention;

            public void Replace(long revision, VisualElementRetention? retention, VisualElementQueryResult? result)
            {
                _retention?.Dispose();
                _retention = retention;
                _result = result;
                Revision = revision;
            }

            public AutomationAnchor? TakeAnchor(VisualElementQueryRequest request)
            {
                if (_result is null || _retention is null) return null;

                var result = _result.Element.Query(request);
                var anchor = new AutomationAnchor(_retention, result);
                _retention = null;
                _result = null;
                return anchor;
            }

            public void Dispose()
            {
                DisposeHelper.DisposeToDefault(ref _retention);
                _result = null;
            }
        }
    }
}