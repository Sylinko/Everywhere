using Avalonia.Input;
using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

public sealed partial class AutomationHostSession
{
    private const int CaptureChunkSize = 128 * 1024;

    /// <inheritdoc />
    public async ValueTask<AcquireAutomationAnchorResponse> AcquireAnchorAsync(
        AcquireAutomationAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        var context = GetContext(request.ContextId);
        try
        {
            return await context.ExecuteAsync(
                (resource, token) => ValueTask.FromResult(resource.AcquireAnchor(request, token)),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _resources.RegisterAsync(request.AnchorId, new AutomationAnchorRelease(context, request.AnchorId)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask<AcquireAutomationAnchorResponse> MoveAnchorAsync(
        MoveAutomationAnchorRequest request,
        CancellationToken cancellationToken = default)
    {
        var sourceContext = GetContext(request.SourceContextId);
        var destinationContext = GetContext(request.DestinationContextId);
        if (ReferenceEquals(sourceContext, destinationContext))
        {
            throw new ArgumentException("A cross-Context anchor move requires distinct source and destination Contexts.", nameof(request));
        }

        AutomationAnchorMovePreparation? preparation = null;
        var isDestinationCreated = false;
        var isSourceConsumed = false;
        try
        {
            preparation = await sourceContext.ExecuteAsync(
                resource => resource.PrepareAnchorMove(request.SourceAnchorId),
                cancellationToken).ConfigureAwait(false);
            var response = await destinationContext.ExecuteAsync(
                (resource, token) => ValueTask.FromResult(resource.AdoptAnchor(request, preparation.Element, token)),
                cancellationToken).ConfigureAwait(false);
            isDestinationCreated = true;

            await sourceContext.ExecuteAsync(
                resource =>
                {
                    resource.CommitAnchorMove(request.SourceAnchorId, preparation);
                    return default(RpcAck);
                },
                CancellationToken.None).ConfigureAwait(false);
            isSourceConsumed = true;
            _resources.Consume(request.SourceAnchorId);
            return response;
        }
        catch
        {
            if (isDestinationCreated && !isSourceConsumed)
            {
                await destinationContext.ReleaseAnchorAsync(request.DestinationAnchorId).ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            if (preparation is not null && !isSourceConsumed)
            {
                await sourceContext.ReleaseAnchorMovePreparationAsync(preparation).ConfigureAwait(false);
            }

            await _resources.RegisterAsync(
                request.DestinationAnchorId,
                new AutomationAnchorRelease(destinationContext, request.DestinationAnchorId)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public ValueTask<AutomationVisualQueryResponse> BuildAnchorsAsync(
        BuildAutomationAnchorsRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => resource.BuildAnchorsAsync(request, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<AutomationCaptureFrame> CaptureAsync(
        CaptureAutomationVisualRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var capture = await GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => resource.CaptureAsync(request, token),
            cancellationToken).ConfigureAwait(false);
        using (capture)
        {
            var header = capture.ToHeader();
            yield return header;
            for (var offset = 0; offset < header.DataLength; offset += CaptureChunkSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var length = Math.Min(CaptureChunkSize, header.DataLength - offset);
                var data = new byte[length];
                Marshal.Copy(capture.Data + offset, data, 0, length);
                yield return new AutomationCaptureChunk { Data = data };
            }
        }
    }

    /// <inheritdoc />
    public ValueTask<RpcAck> ExecuteActionsAsync(
        ExecuteAutomationActionsRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => resource.ExecuteActionsAsync(request, token),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AutomationVisualQueryResponse> ListWindowsAsync(
        ListAutomationWindowsRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetContext(request.ContextId).ExecuteAsync(
            resource => resource.ListWindows(request),
            cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask<AcquireAutomationAnchorResponse> GetElementSnapshotAsync(
        GetAutomationElementSnapshotRequest request,
        CancellationToken cancellationToken = default)
    {
        return GetContext(request.ContextId).ExecuteAsync(
            resource => resource.GetElementSnapshot(request),
            cancellationToken);
    }

    private sealed class AutomationAnchorRelease(AutomationContextResource context, long anchorId) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => context.ReleaseAnchorAsync(anchorId);
    }

    internal sealed record AutomationAnchorMovePreparation(
        VisualElementRetention SourceAnchorRetention,
        VisualElementRetention Retention,
        VisualElement Element
    );

    private sealed partial class AutomationContextResource
    {
        private readonly Dictionary<long, AutomationAnchor> _anchors = [];

        public AcquireAutomationAnchorResponse AcquireAnchor(AcquireAutomationAnchorRequest request, CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.AnchorId);
            cancellationToken.ThrowIfCancellationRequested();
            if (_anchors.ContainsKey(request.AnchorId))
            {
                throw new InvalidOperationException($"Automation anchor {request.AnchorId} is already registered in this Context.");
            }

            var retention = Context.CreateRetention();
            try
            {
                var result = _backend.Query(
                    retention,
                    request.ToLocator(),
                    request.Resolution,
                    new VisualElementQueryRequest(request.RequestedFields, request.MaxTextCharacters));
                cancellationToken.ThrowIfCancellationRequested();
                if (result is null)
                {
                    retention.Dispose();
                    return result.ToResponse();
                }

                _anchors.Add(request.AnchorId, new AutomationAnchor(retention, result));
                return result.ToResponse();
            }
            catch
            {
                retention.Dispose();
                throw;
            }
        }

        public AutomationAnchorMovePreparation PrepareAnchorMove(long anchorId)
        {
            var anchor = GetAnchor(anchorId);
            var retention = Context.CreateRetention();
            try
            {
                retention.Retain(anchor.Result.Element);
                return new AutomationAnchorMovePreparation(anchor.Retention, retention, anchor.Result.Element);
            }
            catch
            {
                retention.Dispose();
                throw;
            }
        }

        public AcquireAutomationAnchorResponse AdoptAnchor(
            MoveAutomationAnchorRequest request,
            VisualElement sourceElement,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.DestinationAnchorId);
            if (_anchors.ContainsKey(request.DestinationAnchorId))
            {
                throw new InvalidOperationException($"Automation anchor {request.DestinationAnchorId} is already registered in this Context.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var retention = Context.CreateRetention();
            try
            {
                var element = sourceElement.Adopt(retention);
                var result = element.Query(new VisualElementQueryRequest(request.RequestedFields, request.MaxTextCharacters));
                cancellationToken.ThrowIfCancellationRequested();
                _anchors.Add(request.DestinationAnchorId, new AutomationAnchor(retention, result));
                return result.ToResponse();
            }
            catch
            {
                retention.Dispose();
                throw; // TODO: How to handle this?
            }
        }

        public void CommitAnchorMove(long sourceAnchorId, AutomationAnchorMovePreparation preparation)
        {
            if (!_anchors.TryGetValue(sourceAnchorId, out var anchor) || !ReferenceEquals(anchor.Retention, preparation.SourceAnchorRetention))
            {
                throw new InvalidOperationException($"Automation anchor {sourceAnchorId} changed before its move committed.");
            }

            _anchors.Remove(sourceAnchorId);
            anchor.Retention.Dispose();
            preparation.Retention.Dispose();
        }

        public async ValueTask<AutomationVisualQueryResponse> BuildAnchorsAsync(
            BuildAutomationAnchorsRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumNodes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TargetTokenBudget);
            var elements = new VisualElement[request.AnchorIds.Length];
            for (var index = 0; index < request.AnchorIds.Length; index++)
            {
                var anchorId = request.AnchorIds[index];
                ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorId);
                elements[index] = GetAnchor(anchorId).Result.Element;
            }

            var defaultLimits = VisualContextSnapshotLimits.Default;
            var limits = defaultLimits with
            {
                MaximumNodes = request.MaximumNodes,
                MaximumChildrenPerNode = Math.Min(defaultLimits.MaximumChildrenPerNode, request.MaximumNodes),
            };
            var result = await new VisualQuery(Context).BuildAsync(
                elements,
                new VisualContextPromptOptions { TargetTokenBudget = request.TargetTokenBudget },
                limits,
                request.Directions,
                cancellationToken).ConfigureAwait(false);
            return ToResponse(result);
        }

        public async ValueTask<IVisualElementCapture> CaptureAsync(
            CaptureAutomationVisualRequest request,
            CancellationToken cancellationToken)
        {
            using var retention = Context.CreateRetention();
            var element = request.ReferenceKind switch
            {
                AutomationVisualReferenceKind.Anchor => RetainAnchor(request.ReferenceId, retention),
                AutomationVisualReferenceKind.Target => RetainTarget(request.ReferenceId, retention),
                _ => throw new ArgumentOutOfRangeException(nameof(request.ReferenceKind), request.ReferenceKind, null),
            };
            return await element.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask<RpcAck> ExecuteActionsAsync(
            ExecuteAutomationActionsRequest request,
            CancellationToken cancellationToken)
        {
            using var retention = Context.CreateRetention();
            foreach (var action in request.Actions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (action.Kind)
                {
                    case AutomationActionKind.Invoke:
                    {
                        RetainTarget(RequireTarget(action), retention).Invoke();
                        break;
                    }
                    case AutomationActionKind.SetText:
                    {
                        RetainTarget(RequireTarget(action), retention).SetText(action.Text ?? string.Empty);
                        break;
                    }
                    case AutomationActionKind.SendKey:
                    {
                        if (action.Key == Key.None) throw new ArgumentException("A key is required for a SendKey action.", nameof(request));
                        RetainTarget(RequireTarget(action), retention).SendKeyGesture(new KeyGesture(action.Key, action.KeyModifiers));
                        break;
                    }
                    case AutomationActionKind.Wait:
                    {
                        if (action.DelayMilliseconds is not { } delayMilliseconds)
                        {
                            throw new ArgumentException("A delay is required for a Wait action.", nameof(request));
                        }

                        ArgumentOutOfRangeException.ThrowIfNegative(delayMilliseconds);
                        await Task.Delay(delayMilliseconds, cancellationToken).ConfigureAwait(false);
                        break;
                    }
                    default:
                    {
                        throw new ArgumentOutOfRangeException(nameof(action.Kind), action.Kind, null);
                    }
                }
            }

            return default;
        }

        public AutomationVisualQueryResponse ListWindows(ListAutomationWindowsRequest request)
        {
            var result = VisualWindowQuery.Build(Context, _backend, request.TargetTokenBudget);
            return ToResponse(result);
        }

        public AcquireAutomationAnchorResponse GetElementSnapshot(GetAutomationElementSnapshotRequest request)
        {
            var result = GetAnchor(request.AnchorId).Result.Element.Query(
                new VisualElementQueryRequest(request.RequestedFields, request.MaxTextCharacters));
            return result.ToResponse();
        }

        public ValueTask ReleaseAnchorAsync(long anchorId)
        {
            var workItem = new ContextWorkItem<RpcAck>(
                (resource, _) =>
                {
                    if (resource._anchors.Remove(anchorId, out var anchor)) anchor.Retention.Dispose();
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

        public ValueTask ReleaseAnchorMovePreparationAsync(AutomationAnchorMovePreparation preparation)
        {
            var workItem = new ContextWorkItem<RpcAck>(
                (_, _) =>
                {
                    preparation.Retention.Dispose();
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

        private static long RequireTarget(AutomationActionStep action) =>
            action.TargetId ?? throw new ArgumentException($"A target is required for a {action.Kind} action.", nameof(action));

        private AutomationAnchor GetAnchor(long anchorId) => _anchors.TryGetValue(anchorId, out var anchor) ?
            anchor :
            throw new InvalidOperationException($"Automation anchor {anchorId} is no longer available.");

        private VisualElement RetainAnchor(long anchorId, VisualElementRetention retention)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(anchorId);
            var element = GetAnchor(anchorId).Result.Element;
            retention.Retain(element);
            return element;
        }

        private VisualElement RetainTarget(long targetId, VisualElementRetention retention)
        {
            if (targetId is <= 0 or > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(targetId));
            }
            if (!Context.TryGetTarget((int)targetId, out var target))
            {
                throw new InvalidOperationException($"Visual target {targetId} is no longer available.");
            }
            if (target is not ElementTarget elementTarget)
            {
                throw new InvalidOperationException($"Visual target {targetId} does not represent one actionable platform element.");
            }

            retention.Retain(elementTarget.Element);
            return elementTarget.Element;
        }

        private sealed record AutomationAnchor(VisualElementRetention Retention, VisualElementQueryResult Result);
    }
}