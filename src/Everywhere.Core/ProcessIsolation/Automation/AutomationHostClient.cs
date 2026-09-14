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
            (id, resourceId) => new RemoteVisualContext(id, resourceId, _rpc, _releaseQueue),
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
        var resourceId = _releaseQueue.AllocateResourceId();
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
    private readonly RpcSafeHandleReleaseQueue _releaseQueue;

    internal RemoteVisualContext(
        Guid id,
        long resourceId,
        IAutomationHostRpc rpc,
        RpcSafeHandleReleaseQueue releaseQueue
    ) : base(resourceId, releaseQueue)
    {
        Id = id;
        _rpc = rpc;
        _releaseQueue = releaseQueue;
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
        VisualElementResolution resolution = VisualElementResolution.TopLevel,
        VisualContextTraverseDirections directions = VisualContextTraverseDirections.All,
        int maximumNodes = VisualQueryRequest.DefaultLimit,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        return await _rpc.BuildDefaultAsync(
            new BuildDefaultVisualContextRequest
            {
                ContextId = lease.ResourceId,
                Resolution = resolution,
                Directions = directions,
                MaximumNodes = maximumNodes,
                TargetTokenBudget = targetTokenBudget,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Queries one retained Agent target through the Host's existing VisualQuery pipeline.</summary>
    public async ValueTask<AutomationVisualQueryResponse> QueryTargetAsync(
        int targetId,
        VisualContextTraverseDirections directions = VisualContextTraverseDirections.All,
        int offset = 1,
        int limit = VisualQueryRequest.DefaultLimit,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default)
    {
        using var lease = AcquireLease();
        return await _rpc.QueryTargetAsync(
            new QueryAutomationTargetRequest
            {
                ContextId = lease.ResourceId,
                TargetId = targetId,
                Directions = directions,
                Offset = offset,
                Limit = limit,
                TargetTokenBudget = targetTokenBudget,
            },
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
        var pickerId = _releaseQueue.AllocateResourceId();
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
        var anchorId = _releaseQueue.AllocateResourceId();
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
        var destinationAnchorId = _releaseQueue.AllocateResourceId();
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
        VisualContextTraverseDirections directions = VisualContextTraverseDirections.All,
        int maximumNodes = VisualQueryRequest.DefaultLimit,
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
                    Directions = directions,
                    MaximumNodes = maximumNodes,
                    TargetTokenBudget = targetTokenBudget,
                },
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
        AutomationCaptureHeader? header = null;
        byte[]? data = null;
        var offset = 0;
        await foreach (var frame in _rpc.CaptureAsync(request, cancellationToken).ConfigureAwait(false))
        {
            switch (frame)
            {
                case AutomationCaptureHeader captureHeader when header is null:
                {
                    if (captureHeader.PixelWidth is <= 0 or > IVisualElementCapture.MaximumDimension ||
                        captureHeader.PixelHeight is <= 0 or > IVisualElementCapture.MaximumDimension ||
                        captureHeader.Stride <= 0 ||
                        captureHeader.DataLength != checked(captureHeader.Stride * captureHeader.PixelHeight))
                    {
                        throw new InvalidDataException("The Automation capture header contains inconsistent bitmap dimensions.");
                    }

                    header = captureHeader;
                    data = new byte[captureHeader.DataLength];
                    break;
                }
                case AutomationCaptureHeader:
                {
                    throw new InvalidDataException("The Automation capture stream contains more than one header.");
                }
                case AutomationCaptureChunk chunk when data is not null:
                {
                    if (offset > data.Length - chunk.Data.Length)
                    {
                        throw new InvalidDataException("The Automation capture stream exceeds its declared data length.");
                    }

                    chunk.Data.CopyTo(data, offset);
                    offset += chunk.Data.Length;
                    break;
                }
                case AutomationCaptureChunk:
                {
                    throw new InvalidDataException("The Automation capture stream started with pixel data instead of a header.");
                }
                default:
                {
                    throw new InvalidDataException($"The Automation capture stream contains an unsupported frame type '{frame.GetType().Name}'.");
                }
            }
        }

        if (header is null || data is null) throw new InvalidDataException("The Automation capture stream did not contain a header.");
        if (offset != data.Length) throw new InvalidDataException("The Automation capture stream ended before its declared data length.");
        return new RemoteVisualElementCapture(header, data);
    }
}