using Everywhere.Automation;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>Creates Main-owned remote visual Context handles on one Automation Host connection.</summary>
public sealed class AutomationHostClient
{
    private readonly RpcConnection _connection;
    private readonly AutomationHostRpcClient _rpc;
    private readonly RpcSafeHandleReleaseQueue _releaseQueue;

    /// <summary>Initializes a client for one authenticated Automation Host connection incarnation.</summary>
    public AutomationHostClient(RpcConnection connection)
    {
        connection.RegisterExceptionMapper(AutomationRpcExceptionMapper.Shared);
        _connection = connection;
        _rpc = new AutomationHostRpcClient(connection);
        _releaseQueue = connection.GetSafeHandleReleaseQueue();
    }

    /// <summary>Creates a remote Context and takes ownership of its Host-assigned identity and resource ID.</summary>
    public async ValueTask<RemoteVisualContext> CreateContextAsync(
        int maximumRetainedTurnCount = VisualContext.DefaultMaximumRetainedTurnCount,
        int maximumRetainedTargetCount = VisualContext.DefaultMaximumRetainedTargetCount,
        CancellationToken cancellationToken = default)
    {
        return await CreateContextAsync(
            (id, resourceId) => new RemoteVisualContext(id, resourceId, _connection, _rpc, _releaseQueue),
            maximumRetainedTurnCount,
            maximumRetainedTargetCount,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates a diagnostics-capable remote Context with no completed-turn retention.</summary>
    public async ValueTask<RemoteDebuggerVisualContext> CreateDebuggerContextAsync(CancellationToken cancellationToken = default)
    {
        return await CreateContextAsync(
            (id, resourceId) => new RemoteDebuggerVisualContext(
                id,
                resourceId,
                _connection,
                _rpc,
                new AutomationHostDiagnosticsRpcClient(_connection),
                _releaseQueue),
            maximumRetainedTurnCount: 0,
            maximumRetainedTargetCount: 0,
            cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TContext> CreateContextAsync<TContext>(
        Func<Guid, long, TContext> createContext,
        int maximumRetainedTurnCount,
        int maximumRetainedTargetCount,
        CancellationToken cancellationToken)
        where TContext : RemoteVisualContext
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resourceId = _connection.AllocateResourceId();
        try
        {
            var id = await _rpc.CreateContextAsync(
                new CreateAutomationContextRequest
                {
                    ContextResourceId = resourceId,
                    MaximumRetainedTurnCount = maximumRetainedTurnCount,
                    MaximumRetainedTargetCount = maximumRetainedTargetCount,
                },
                cancellationToken).ConfigureAwait(false);
            return createContext(id, resourceId);
        }
        catch
        {
            _releaseQueue.TryQueueRelease(resourceId);
            throw;
        }
    }
}

/// <summary>
/// Main-side ownership token and coarse-grained client for one Host-owned visual Context.
/// </summary>
public class RemoteVisualContext : RpcSafeHandle
{
    /// <summary>Gets the globally unique identity of the Host-owned visual Context.</summary>
    public Guid Id { get; }

    /// <summary>Gets whether this Context's originating Host connection has closed.</summary>
    public bool IsConnectionClosed => _releaseQueue.IsConnectionClosed;

    private readonly IAutomationHostRpc _rpc;
    private readonly RpcConnection _connection;
    private readonly RpcSafeHandleReleaseQueue _releaseQueue;

    internal RemoteVisualContext(
        Guid id,
        long resourceId,
        RpcConnection connection,
        IAutomationHostRpc rpc,
        RpcSafeHandleReleaseQueue releaseQueue
    ) : base(resourceId, releaseQueue)
    {
        Id = id;
        _connection = connection;
        _rpc = rpc;
        _releaseQueue = releaseQueue;
    }

    internal long AllocateResourceId() => _connection.AllocateResourceId();

    /// <summary>Starts one native text-selection monitor owned by this Context.</summary>
    public async ValueTask<RemoteTextSelectionMonitorStartResult> StartTextSelectionMonitoringAsync(
        long monitorId,
        int mainProcessId,
        TextSelectionMonitoringConfiguration configuration,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(monitorId);

        var contextLease = AcquireLease();
        try
        {
            var result = await _rpc.StartTextSelectionMonitoringAsync(
                new StartTextSelectionMonitoringRequest
                {
                    ContextId = contextLease.ResourceId,
                    MonitorId = monitorId,
                    MainProcessId = mainProcessId,
                    Configuration = configuration,
                },
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSucceeded)
            {
                contextLease.Dispose();
                return new RemoteTextSelectionMonitorStartResult(result, null);
            }

            return new RemoteTextSelectionMonitorStartResult(
                result,
                new RemoteTextSelectionMonitor(_rpc, contextLease, monitorId, _releaseQueue));
        }
        catch
        {
            _releaseQueue.TryQueueRelease(monitorId);
            contextLease.Dispose();
            throw;
        }
    }

    /// <summary>Accepts one Host-allocated pushed anchor into this remote Context.</summary>
    public RemoteVisualAnchor CreatePushedAnchor(long anchorId, AcquireAutomationAnchorResponse observation)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(anchorId, 0);

        observation.ThrowIfFailed();
        if (!observation.IsAvailable) throw new InvalidDataException("A pushed Automation anchor must contain an available source observation.");
        var contextLease = AcquireLease();
        try
        {
            return new RemoteVisualAnchor(this, contextLease, observation, anchorId, _releaseQueue);
        }
        catch
        {
            contextLease.Dispose();
            throw;
        }
    }

    /// <summary>Ensures that an Agent turn exists without advancing an existing turn.</summary>
    public async ValueTask EnsureTurnAsync(CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        await _rpc.EnsureTurnAsync(new AutomationContextRequest { ContextId = lease.ResourceId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Completes the previous Agent turn and begins a new one.</summary>
    public async ValueTask AdvanceTurnAsync(CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        await _rpc.AdvanceTurnAsync(new AutomationContextRequest { ContextId = lease.ResourceId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Acquires a platform-default root and returns the Host-built final visual projection.</summary>
    public async ValueTask<AutomationVisualQueryResponse> BuildDefaultAsync(
        VisualQueryRequest request,
        VisualElementResolution resolution = VisualElementResolution.TopLevel,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        return await _rpc.BuildDefaultAsync(
            new BuildDefaultVisualContextRequest
            {
                ContextId = lease.ResourceId,
                Resolution = resolution,
                Query = request,
                TargetTokenBudget = targetTokenBudget,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queries one retained Agent target through the Host's existing VisualQuery pipeline.</summary>
    public async ValueTask<AutomationVisualQueryResponse> QueryTargetAsync(
        int targetId,
        VisualQueryRequest request,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        return await _rpc.QueryTargetAsync(
            new QueryAutomationTargetRequest
            {
                ContextId = lease.ResourceId,
                TargetId = targetId,
                Query = request,
                TargetTokenBudget = targetTokenBudget,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queries one retained Agent target and delivers owned scan captures before returning the final projection.</summary>
    public async ValueTask<AutomationVisualQueryResponse> QueryTargetWithCapturesAsync(
        int targetId,
        VisualQueryRequest request,
        int targetTokenBudget,
        Action<IVisualElementCapture> captureReceiver,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        return await ReceiveQueryAsync(
            _rpc.QueryTargetWithCapturesAsync(
                new QueryAutomationTargetRequest
                {
                    ContextId = lease.ResourceId,
                    TargetId = targetId,
                    Query = request,
                    TargetTokenBudget = targetTokenBudget,
                },
                cancellationToken),
            captureReceiver,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads one bounded text page from a retained Agent target.</summary>
    public async ValueTask<string> ReadTextAsync(
        int targetId,
        int offset = 0,
        int limit = VisualQuery.DefaultTextLimit,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        var response = await _rpc.ReadTextAsync(
            new ReadAutomationTextRequest
            {
                ContextId = lease.ResourceId,
                TargetId = targetId,
                Offset = offset,
                Limit = limit,
            },
            cancellationToken).ConfigureAwait(false);
        return response.Content;
    }

    /// <summary>Creates one Host-owned interactive picker bound to this visual Context.</summary>
    public async ValueTask<RemoteVisualPicker> BeginPickerAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contextLease = AcquireLease();
        var pickerId = _connection.AllocateResourceId();
        try
        {
            await _rpc.BeginPickerAsync(
                new BeginVisualPickerRequest
                {
                    ContextId = contextLease.ResourceId,
                    PickerId = pickerId,
                    MainProcessId = Environment.ProcessId,
                },
                cancellationToken).ConfigureAwait(false);
            return new RemoteVisualPicker(_rpc, this, contextLease, pickerId, _releaseQueue);
        }
        catch
        {
            _releaseQueue.TryQueueRelease(pickerId);
            contextLease.Dispose();
            throw;
        }
    }

    /// <summary>Acquires one Context-owned visual anchor and its requested initial scalar observation.</summary>
    public async ValueTask<RemoteVisualAnchor?> AcquireAnchorAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var contextLease = AcquireLease();
        var anchorId = _connection.AllocateResourceId();
        try
        {
            var response = await _rpc.AcquireAnchorAsync(
                locator.ToAcquireRequest(contextLease.ResourceId, anchorId, resolution, query ?? VisualElementQueryRequest.Default),
                cancellationToken
            ).ConfigureAwait(false);
            if (!response.IsAvailable)
            {
                _releaseQueue.TryQueueRelease(anchorId);
                contextLease.Dispose();
                return null;
            }

            return new RemoteVisualAnchor(this, contextLease, response, anchorId, _releaseQueue);
        }
        catch
        {
            _releaseQueue.TryQueueRelease(anchorId);
            contextLease.Dispose();
            throw;
        }
    }

    internal async ValueTask<RemoteVisualAnchor> MoveAnchorAsync(
        RemoteVisualAnchor sourceAnchor,
        RemoteVisualContext destinationContext,
        VisualElementQueryRequest? query,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(sourceAnchor.Context, this))
        {
            throw new ArgumentException("The source anchor must belong to this remote visual Context.", nameof(sourceAnchor));
        }

        if (ReferenceEquals(destinationContext, this))
        {
            return sourceAnchor;
        }

        if (!ReferenceEquals(_releaseQueue, destinationContext._releaseQueue))
        {
            throw new ArgumentException("Visual anchors can move only between Contexts on the same Host connection.", nameof(destinationContext));
        }

        cancellationToken.ThrowIfCancellationRequested();
        using var sourceLease = sourceAnchor.AcquireLease();
        var destinationContextLease = destinationContext.AcquireLease();
        var destinationAnchorId = _connection.AllocateResourceId();
        try
        {
            var effectiveQuery = query ?? VisualElementQueryRequest.Default;
            var response = await _rpc.MoveAnchorAsync(
                new MoveAutomationAnchorRequest
                {
                    SourceContextId = sourceAnchor.ContextId,
                    SourceAnchorId = sourceLease.ResourceId,
                    DestinationContextId = destinationContextLease.ResourceId,
                    DestinationAnchorId = destinationAnchorId,
                    RequestedFields = effectiveQuery.RequestedFields,
                    MaxTextCharacters = effectiveQuery.MaxTextCharacters,
                },
                cancellationToken).ConfigureAwait(false);
            if (!response.IsAvailable)
            {
                throw new InvalidOperationException("The Automation Host did not create the moved visual anchor.");
            }

            sourceAnchor.Consume();
            return new RemoteVisualAnchor(destinationContext, destinationContextLease, response, destinationAnchorId, _releaseQueue);
        }
        catch
        {
            _releaseQueue.TryQueueRelease(destinationAnchorId);
            destinationContextLease.Dispose();
            throw;
        }
    }

    /// <summary>Builds final model-facing text from Context-owned pre-publication anchors.</summary>
    public async ValueTask<AutomationVisualQueryResponse> BuildAnchorsAsync(
        IReadOnlyList<RemoteVisualAnchor> anchors,
        VisualQueryRequest request,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        var anchorIds = new long[anchors.Count];
        var anchorLeases = new List<RpcSafeHandleLease>(anchors.Count);
        try
        {
            for (var index = 0; index < anchors.Count; index++)
            {
                var anchor = anchors[index];
                if (!ReferenceEquals(anchor.Context, this))
                {
                    throw new ArgumentException("Every anchor must belong to this remote visual Context.", nameof(anchors));
                }

                var anchorLease = anchor.AcquireLease();
                anchorLeases.Add(anchorLease);
                anchorIds[index] = anchorLease.ResourceId;
            }

            return await _rpc.BuildAnchorsAsync(
                new BuildAutomationAnchorsRequest
                {
                    ContextId = contextLease.ResourceId,
                    AnchorIds = anchorIds,
                    Query = request,
                    TargetTokenBudget = targetTokenBudget,
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var anchorLease in anchorLeases) anchorLease.Dispose();
        }
    }

    /// <summary>Builds from pre-publication anchors and delivers owned scan captures before returning the final projection.</summary>
    public async ValueTask<AutomationVisualQueryResponse> BuildAnchorsWithCapturesAsync(
        IReadOnlyList<RemoteVisualAnchor> anchors,
        VisualQueryRequest request,
        int targetTokenBudget,
        Action<IVisualElementCapture> captureReceiver,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        var anchorIds = new long[anchors.Count];
        var anchorLeases = new List<RpcSafeHandleLease>(anchors.Count);
        try
        {
            for (var index = 0; index < anchors.Count; index++)
            {
                var anchor = anchors[index];
                if (!ReferenceEquals(anchor.Context, this))
                {
                    throw new ArgumentException("Every anchor must belong to this remote visual Context.", nameof(anchors));
                }

                var anchorLease = anchor.AcquireLease();
                anchorLeases.Add(anchorLease);
                anchorIds[index] = anchorLease.ResourceId;
            }

            return await ReceiveQueryAsync(
                _rpc.BuildAnchorsWithCapturesAsync(
                    new BuildAutomationAnchorsRequest
                    {
                        ContextId = contextLease.ResourceId,
                        AnchorIds = anchorIds,
                        Query = request,
                        TargetTokenBudget = targetTokenBudget,
                    },
                    cancellationToken),
                captureReceiver,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            foreach (var anchorLease in anchorLeases) anchorLease.Dispose();
        }
    }

    /// <summary>Captures one retained Agent target as an owned raw bitmap.</summary>
    public async ValueTask<IVisualElementCapture> CaptureTargetAsync(int targetId, CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        return await ReceiveCaptureAsync(
            new CaptureAutomationVisualRequest
            {
                ContextId = contextLease.ResourceId,
                ReferenceKind = AutomationVisualReferenceKind.Target,
                ReferenceId = targetId,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures one pre-publication anchor as an owned raw bitmap.</summary>
    public async ValueTask<IVisualElementCapture> CaptureAnchorAsync(
        RemoteVisualAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(anchor.Context, this))
        {
            throw new ArgumentException("The anchor must belong to this remote visual Context.", nameof(anchor));
        }

        using var contextLease = AcquireLease();
        using var anchorLease = anchor.AcquireLease();
        return await ReceiveCaptureAsync(
            new CaptureAutomationVisualRequest
            {
                ContextId = contextLease.ResourceId,
                ReferenceKind = AutomationVisualReferenceKind.Anchor,
                ReferenceId = anchorLease.ResourceId,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Executes an ordered action batch after the Main process has authorized it.</summary>
    public async ValueTask ExecuteActionsAsync(
        IReadOnlyList<AutomationActionStep> actions,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        await _rpc.ExecuteActionsAsync(
            new ExecuteAutomationActionsRequest
            {
                ContextId = contextLease.ResourceId,
                Actions = [.. actions],
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Lists current top-level windows and publishes their Agent target IDs.</summary>
    public async ValueTask<AutomationVisualQueryResponse> ListWindowsAsync(
        int targetTokenBudget = VisualWindowQuery.DefaultTokenBudget,
        CancellationToken cancellationToken = default)
    {
        using var contextLease = AcquireLease();
        return await _rpc.ListWindowsAsync(
            new ListAutomationWindowsRequest
            {
                ContextId = contextLease.ResourceId,
                TargetTokenBudget = targetTokenBudget,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns a fresh field-selected scalar observation of one pre-publication anchor.</summary>
    public async ValueTask<VisualElementSnapshot> GetElementSnapshotAsync(
        RemoteVisualAnchor anchor,
        VisualElementFields requestedFields,
        int maxTextCharacters = 0,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(anchor.Context, this))
        {
            throw new ArgumentException("The anchor must belong to this remote visual Context.", nameof(anchor));
        }

        using var contextLease = AcquireLease();
        using var anchorLease = anchor.AcquireLease();
        var response = await _rpc.GetElementSnapshotAsync(
            new GetAutomationElementSnapshotRequest
            {
                ContextId = contextLease.ResourceId,
                AnchorId = anchorLease.ResourceId,
                RequestedFields = requestedFields,
                MaxTextCharacters = maxTextCharacters,
            },
            cancellationToken).ConfigureAwait(false);

        response.ThrowIfFailed();
        if (!response.IsAvailable)
        {
            throw new InvalidOperationException("The remote visual anchor is no longer available.");
        }

        return response.ToSnapshot();
    }

    private async ValueTask<IVisualElementCapture> ReceiveCaptureAsync(
        CaptureAutomationVisualRequest request,
        CancellationToken cancellationToken)
    {
        var assembler = new CaptureStreamAssembler();
        RemoteVisualElementCapture? result = null;
        try
        {
            await foreach (var frame in _rpc.CaptureAsync(request, cancellationToken).ConfigureAwait(false))
            {
                if (result is not null)
                {
                    throw new InvalidDataException("The Automation capture stream contains data after its completed capture.");
                }

                switch (frame)
                {
                    case AutomationCaptureHeader header:
                    {
                        assembler.Begin(header);
                        break;
                    }
                    case AutomationCaptureChunk chunk:
                    {
                        result = assembler.Append(chunk);
                        break;
                    }
                    default:
                    {
                        throw new InvalidDataException($"The Automation capture stream contains an unsupported frame type '{frame.GetType().Name}'.");
                    }
                }
            }

            assembler.EnsureNoIncompleteCapture();
            return result ?? throw new InvalidDataException("The Automation capture stream did not contain a complete capture.");
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    private static async ValueTask<AutomationVisualQueryResponse> ReceiveQueryAsync(
        IAsyncEnumerable<AutomationVisualQueryFrame> frames,
        Action<IVisualElementCapture> captureReceiver,
        CancellationToken cancellationToken)
    {
        var assembler = new CaptureStreamAssembler();
        AutomationVisualQueryResponse? result = null;

        await foreach (var frame in frames.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (result is not null)
            {
                var message = frame is AutomationVisualQueryResultFrame ?
                    "The Automation query stream contains more than one result." :
                    "The Automation query stream contains data after its final result.";
                throw new InvalidDataException(message);
            }

            switch (frame)
            {
                case AutomationVisualQueryCaptureFrame { Capture: AutomationCaptureHeader header }:
                {
                    assembler.Begin(header);
                    break;
                }
                case AutomationVisualQueryCaptureFrame { Capture: AutomationCaptureChunk chunk }:
                {
                    var capture = assembler.Append(chunk);
                    if (capture is not null) DeliverCapture(capture);
                    break;
                }
                case AutomationVisualQueryCaptureFrame captureFrame:
                {
                    var frameType = captureFrame.Capture.GetType().Name;
                    throw new InvalidDataException($"The Automation query stream contains an unsupported capture frame type '{frameType}'.");
                }
                case AutomationVisualQueryResultFrame resultFrame:
                {
                    assembler.EnsureNoIncompleteCapture();
                    result = resultFrame.Result ?? throw new InvalidDataException("The Automation query stream contains an empty final result.");
                    break;
                }
                default:
                {
                    throw new InvalidDataException($"The Automation query stream contains an unsupported frame type '{frame.GetType().Name}'.");
                }
            }

        }

        assembler.EnsureNoIncompleteCapture();
        return result ?? throw new InvalidDataException("The Automation query stream ended without a final result.");

        void DeliverCapture(RemoteVisualElementCapture completedCapture)
        {
            var capture = completedCapture;
            try
            {
                captureReceiver(capture);
                capture = null;
            }
            finally
            {
                capture?.Dispose();
            }
        }
    }

    private sealed class CaptureStreamAssembler
    {
        private AutomationCaptureHeader? _header;
        private byte[]? _data;
        private int _offset;

        public void Begin(AutomationCaptureHeader header)
        {
            if (_header is not null)
            {
                throw new InvalidDataException("The Automation capture stream contains a new header before completing the current capture.");
            }

            var bytesPerPixel = header.PixelFormat switch
            {
                AutomationCapturePixelFormat.Bgra8888 or AutomationCapturePixelFormat.Rgba8888 or AutomationCapturePixelFormat.Rgb32 => 4,
                AutomationCapturePixelFormat.Rgb565 => 2,
                AutomationCapturePixelFormat.Alpha8 => 1,
                _ => throw new InvalidDataException("The Automation capture header contains an unsupported pixel format."),
            };

            if (header.AlphaFormat is
                not AutomationCaptureAlphaFormat.Opaque
                and not AutomationCaptureAlphaFormat.Premultiplied
                and not AutomationCaptureAlphaFormat.Unpremultiplied)
            {
                throw new InvalidDataException("The Automation capture header contains an unsupported alpha format.");
            }

            // Validate the source row layout before allocating or handing pixels to native image code.
            // Use wide arithmetic so malformed strides cannot overflow the declared buffer length check.
            if (header.PixelWidth is <= 0 or > IVisualElementCapture.MaximumDimension ||
                header.PixelHeight is <= 0 or > IVisualElementCapture.MaximumDimension ||
                header.Stride < (long)header.PixelWidth * bytesPerPixel ||
                header.DataLength != (long)header.Stride * header.PixelHeight)
            {
                throw new InvalidDataException("The Automation capture header contains inconsistent bitmap dimensions.");
            }

            _header = header;
            _data = new byte[header.DataLength];
            _offset = 0;
        }

        public RemoteVisualElementCapture? Append(AutomationCaptureChunk chunk)
        {
            if (_header is null || _data is null)
            {
                throw new InvalidDataException("The Automation capture stream started with pixel data instead of a header.");
            }

            if (_offset > _data.Length - chunk.Data.Length)
            {
                throw new InvalidDataException("The Automation capture stream exceeds its declared data length.");
            }

            chunk.Data.CopyTo(_data, _offset);
            _offset += chunk.Data.Length;
            if (_offset != _data.Length) return null;

            var capture = new RemoteVisualElementCapture(_header, _data);
            _header = null;
            _data = null;
            _offset = 0;
            return capture;
        }

        public void EnsureNoIncompleteCapture()
        {
            if (_header is not null)
            {
                throw new InvalidDataException("The Automation capture stream ended before its declared data length.");
            }
        }
    }
}