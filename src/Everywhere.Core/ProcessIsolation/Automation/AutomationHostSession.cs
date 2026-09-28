using System.Threading.Channels;
using Avalonia.Platform;
using Everywhere.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Owns the shared platform Backend and every visual Context created by one authenticated Main connection.
/// </summary>
[InHostProcess(ProcessRole.Automation)]
public sealed partial class AutomationHostSession : IProcessRoleSession, IAutomationHostRpc, IAutomationHostDiagnosticsRpc
{
    private readonly IVisualElementBackend _backend;
    private readonly IVisualPickerResolver _pickerResolver;
    private readonly Lock _drainGate = new();
    private readonly RpcRemoteResourceRegistry _resources = new();
    private Task? _drainTask;
    private int _isDraining;

    /// <summary>Creates a connection session and assumes ownership of the supplied Backend.</summary>
    public AutomationHostSession(IVisualElementBackend backend)
    {
        _backend = backend;
        _pickerResolver = DefaultVisualPickerResolver.Shared;
    }

    /// <summary>Creates a connection session with a platform-aware interactive picker resolver.</summary>
    public AutomationHostSession(IVisualElementBackend backend, IVisualPickerResolver pickerResolver)
    {
        _backend = backend;
        _pickerResolver = pickerResolver;
    }

    /// <inheritdoc />
    public void Bind(RpcConnection connection)
    {
        connection.RegisterExceptionMapper(AutomationRpcExceptionMapper.Shared);
        _resources.Bind(connection);
        AutomationHostRpcBinding.Bind(connection, this);
        AutomationHostDiagnosticsRpcBinding.Bind(connection, this);
    }

    /// <inheritdoc />
    public void OnAuthenticated(RpcHandshake peer)
    {
    }

    /// <inheritdoc />
    public async ValueTask<Guid> CreateContextAsync(CreateAutomationContextRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDraining) != 0, this);
        var resource = new AutomationContextResource(
            new VisualContext(request.MaximumRetainedTurnCount, request.MaximumRetainedTargetCount),
            _backend,
            _pickerResolver);
        var contextId = resource.Context.Id;
        await _resources.RegisterAsync(request.ContextResourceId, resource).ConfigureAwait(false);
        return contextId;
    }

    /// <inheritdoc />
    public ValueTask<RpcAck> EnsureTurnAsync(AutomationContextRequest request, CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            static resource =>
            {
                resource.EnsureTurn();
                return default(RpcAck);
            },
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<RpcAck> AdvanceTurnAsync(AutomationContextRequest request, CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            static resource =>
            {
                resource.AdvanceTurn();
                return default(RpcAck);
            },
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<AutomationVisualQueryResponse> BuildDefaultAsync(
        BuildDefaultVisualContextRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => resource.BuildDefaultAsync(request, token),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<AutomationVisualQueryResponse> QueryTargetAsync(
        QueryAutomationTargetRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            (resource, token) => resource.QueryTargetAsync(request, token),
            cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<AutomationVisualQueryFrame> QueryTargetWithCapturesAsync(
        QueryAutomationTargetRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteStreamAsync(
            (resource, token) => resource.QueryTargetWithCapturesAsync(request, token),
            cancellationToken);

    /// <inheritdoc />
    public ValueTask<ReadAutomationTextResponse> ReadTextAsync(
        ReadAutomationTextRequest request,
        CancellationToken cancellationToken = default) =>
        GetContext(request.ContextId).ExecuteAsync(
            resource => new ReadAutomationTextResponse
            {
                Content = new VisualQuery(resource.Context).ReadText(request.TargetId, request.Offset, request.Limit),
            },
            cancellationToken);

    /// <inheritdoc />
    public ValueTask BeginDrainingAsync(CancellationToken cancellationToken = default)
    {
        Task drainTask;
        lock (_drainGate)
        {
            drainTask = _drainTask ??= DrainAsync();
        }

        return new ValueTask(drainTask.WaitAsync(cancellationToken));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await BeginDrainingAsync().ConfigureAwait(false);

    private AutomationContextResource GetContext(long contextId)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _isDraining) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(contextId);
        return _resources.TryGet<AutomationContextResource>(contextId, out var context) ?
            context :
            throw new InvalidOperationException($"Automation Context {contextId} is no longer available.");
    }

    private async Task DrainAsync()
    {
        Interlocked.Exchange(ref _isDraining, 1);
        try
        {
            await _resources.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _backend.Dispose();
        }
    }

    private sealed partial class AutomationContextResource : IAsyncDisposable
    {
        public VisualContext Context { get; }

        private readonly IVisualElementBackend _backend;
        private readonly IVisualPickerResolver _pickerResolver;
        private readonly Channel<IContextWorkItem> _operations = Channel.CreateUnbounded<IContextWorkItem>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        private readonly Lock _queueGate = new();
        private readonly Task _worker;
        private VisualTargetTurn? _turn;
        private bool _isDisposing;

        public AutomationContextResource(VisualContext context, IVisualElementBackend backend, IVisualPickerResolver pickerResolver)
        {
            Context = context;
            _backend = backend;
            _pickerResolver = pickerResolver;
            _worker = Task.Run(ProcessOperationsAsync);
        }

        public ValueTask<T> ExecuteAsync<T>(Func<AutomationContextResource, T> operation, CancellationToken cancellationToken)
        {
            return ExecuteAsync((resource, _) => ValueTask.FromResult(operation(resource)), cancellationToken);
        }

        public ValueTask<T> ExecuteAsync<T>(
            Func<AutomationContextResource, CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken)
        {
            var workItem = new ContextWorkItem<T>(operation, cancellationToken);
            lock (_queueGate)
            {
                ObjectDisposedException.ThrowIf(_isDisposing, this);
                if (!_operations.Writer.TryWrite(workItem)) throw new InvalidOperationException("The Automation Context queue is closed.");
            }

            return new ValueTask<T>(workItem.Completion);
        }

        public IAsyncEnumerable<T> ExecuteStreamAsync<T>(
            Func<AutomationContextResource, CancellationToken, IAsyncEnumerable<T>> operation,
            CancellationToken cancellationToken) =>
            ExecuteStreamCoreAsync(operation, cancellationToken);

        private async IAsyncEnumerable<T> ExecuteStreamCoreAsync<T>(
            Func<AutomationContextResource, CancellationToken, IAsyncEnumerable<T>> operation,
            CancellationToken operationCancellationToken,
            [EnumeratorCancellation] CancellationToken enumerationCancellationToken = default)
        {
            operationCancellationToken.ThrowIfCancellationRequested();
            enumerationCancellationToken.ThrowIfCancellationRequested();
            var workItem = new ContextStreamWorkItem<T>(operation, operationCancellationToken);
            lock (_queueGate)
            {
                ObjectDisposedException.ThrowIf(_isDisposing, this);
                if (!_operations.Writer.TryWrite(workItem)) throw new InvalidOperationException("The Automation Context queue is closed.");
            }

            try
            {
                await foreach (var item in workItem.ReadAllAsync(enumerationCancellationToken).ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            finally
            {
                // Stopping a stream must also stop its serialized Context operation. Otherwise a bounded
                // producer can remain blocked after the RPC peer abandons the response and stall the Context.
                workItem.Cancel();
            }
        }

        public void EnsureTurn() => _turn ??= Context.BeginTurn();

        public void AdvanceTurn()
        {
            _turn?.Complete();
            _turn = null;
            _turn = Context.BeginTurn();
        }

        public async ValueTask<AutomationVisualQueryResponse> BuildDefaultAsync(
            BuildDefaultVisualContextRequest request,
            CancellationToken cancellationToken)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.MaximumNodes);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(request.TargetTokenBudget);
            using var retention = Context.CreateRetention();
            var root = _backend.Query(retention, VisualElementLocator.Default, request.Resolution) ??
                throw new InvalidOperationException("The platform-default visual root is not available.");
            var defaultLimits = VisualContextSnapshotLimits.Default;
            var limits = defaultLimits with
            {
                MaximumNodes = request.MaximumNodes,
                MaximumChildrenPerNode = Math.Min(defaultLimits.MaximumChildrenPerNode, request.MaximumNodes),
            };
            var result = await new VisualQuery(Context).BuildAsync(
                [root.Element],
                new VisualContextPromptOptions { TargetTokenBudget = request.TargetTokenBudget },
                limits,
                request.Directions,
                cancellationToken).ConfigureAwait(false);
            return ToResponse(result);
        }

        public async ValueTask<AutomationVisualQueryResponse> QueryTargetAsync(
            QueryAutomationTargetRequest request,
            CancellationToken cancellationToken)
        {
            var result = await new VisualQuery(Context).ExecuteAsync(
                request.TargetId,
                new VisualQueryRequest
                {
                    Directions = request.Directions,
                    Offset = request.Offset,
                    Limit = request.Limit,
                },
                new VisualContextPromptOptions { TargetTokenBudget = request.TargetTokenBudget },
                cancellationToken).ConfigureAwait(false);
            return ToResponse(result);
        }

        public IAsyncEnumerable<AutomationVisualQueryFrame> QueryTargetWithCapturesAsync(
            QueryAutomationTargetRequest request,
            CancellationToken cancellationToken) =>
            StreamQueryAsync(
                (receiver, token) => new VisualQuery(Context, receiver).ExecuteAsync(
                    request.TargetId,
                    new VisualQueryRequest
                    {
                        Directions = request.Directions,
                        Offset = request.Offset,
                        Limit = request.Limit,
                    },
                    new VisualContextPromptOptions { TargetTokenBudget = request.TargetTokenBudget },
                    token),
                cancellationToken);

        public async ValueTask DisposeAsync()
        {
            Task worker;
            lock (_queueGate)
            {
                if (!_isDisposing)
                {
                    _isDisposing = true;
                    _operations.Writer.TryComplete();
                }

                worker = _worker;
            }

            await worker.ConfigureAwait(false);
        }

        private static AutomationVisualQueryResponse ToResponse(VisualQueryResult result) =>
            new()
            {
                Content = result.Content,
                RepresentedTargetCount = result.RepresentedTargetCount,
            };

        private async IAsyncEnumerable<AutomationVisualQueryFrame> StreamQueryAsync(
            Func<Func<IVisualElementCapture, CancellationToken, ValueTask>, CancellationToken, Task<VisualQueryResult>> query,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var captures = Channel.CreateBounded<IVisualElementCapture>(
                new BoundedChannelOptions(1)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false,
                });
            var queryTask = RunQueryAsync();
            try
            {
                await foreach (var capture in captures.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    using (capture)
                    {
                        yield return new AutomationVisualQueryCaptureFrame { Capture = CreateAlpha8Header(capture) };
                        foreach (var data in ReadAlpha8Chunks(capture, cancellationToken))
                        {
                            yield return new AutomationVisualQueryCaptureFrame { Capture = new AutomationCaptureChunk { Data = data } };
                        }
                    }
                }

                yield return new AutomationVisualQueryResultFrame { Result = ToResponse(await queryTask.ConfigureAwait(false)) };
            }
            finally
            {
                captures.Writer.TryComplete();
                while (captures.Reader.TryRead(out var capture)) capture.Dispose();
                if (!queryTask.IsCompletedSuccessfully)
                {
                    try
                    {
                        await queryTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                }
            }

            async Task<VisualQueryResult> RunQueryAsync()
            {
                try
                {
                    return await query(
                        async (capture, token) => await captures.Writer.WriteAsync(capture, token).ConfigureAwait(false),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    captures.Writer.TryComplete();
                }
            }
        }

        private static AutomationCaptureHeader CreateAlpha8Header(IVisualElementCapture capture) =>
            new()
            {
                BoundsX = capture.Bounds.X,
                BoundsY = capture.Bounds.Y,
                BoundsWidth = capture.Bounds.Width,
                BoundsHeight = capture.Bounds.Height,
                PixelWidth = capture.Size.Width,
                PixelHeight = capture.Size.Height,
                Stride = capture.Size.Width,
                PixelFormat = AutomationCapturePixelFormat.Alpha8,
                AlphaFormat = AutomationCaptureAlphaFormat.Unpremultiplied,
                DataLength = checked(capture.Size.Width * capture.Size.Height),
            };

        private static IEnumerable<byte[]> ReadAlpha8Chunks(IVisualElementCapture capture, CancellationToken cancellationToken)
        {
            var width = capture.Size.Width;
            var height = capture.Size.Height;
            if (width <= 0 || height <= 0) yield break;

            var chunk = new byte[Math.Min(CaptureChunkSize, checked(width * height))];
            var chunkLength = 0;
            if (capture.AlphaFormat == AlphaFormat.Opaque)
            {
                for (var remaining = checked(width * height); remaining > 0;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var length = Math.Min(chunk.Length, remaining);
                    Array.Fill(chunk, byte.MaxValue, 0, length);
                    yield return length == chunk.Length ? chunk : chunk[..length];
                    remaining -= length;
                    chunk = new byte[Math.Min(CaptureChunkSize, Math.Max(1, remaining))];
                }

                yield break;
            }

            if (capture.Format != PixelFormat.Bgra8888 && capture.Format != PixelFormat.Rgba8888)
            {
                throw new NotSupportedException($"The capture pixel format '{capture.Format}' does not expose an 8-bit alpha channel.");
            }

            if (capture.Stride < checked(width * 4))
            {
                throw new InvalidDataException("The capture stride is shorter than its 32-bit pixel row.");
            }

            for (var y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (var x = 0; x < width;)
                {
                    var length = Math.Min(width - x, chunk.Length - chunkLength);
                    CopyAlpha8(capture.Data + y * capture.Stride + x * 4 + 3, chunk.AsSpan(chunkLength, length));
                    x += length;
                    chunkLength += length;
                    if (chunkLength != chunk.Length) continue;

                    yield return chunk;
                    var remaining = checked(width * height - (y * width + x));
                    chunk = new byte[Math.Min(CaptureChunkSize, Math.Max(1, remaining))];
                    chunkLength = 0;
                }
            }

            if (chunkLength > 0) yield return chunk[..chunkLength];
        }

        private static unsafe void CopyAlpha8(nint source, Span<byte> destination)
        {
            var sourceBytes = (byte*)source;
            // TODO: SIMD could extract wider alpha batches after benchmarks justify the added complexity.
            for (var index = 0; index < destination.Length; index++) destination[index] = sourceBytes[index * 4];
        }

        private async Task ProcessOperationsAsync()
        {
            try
            {
                await foreach (var operation in _operations.Reader.ReadAllAsync().ConfigureAwait(false))
                {
                    await operation.ExecuteAsync(this).ConfigureAwait(false);
                }
            }
            finally
            {
                _turn?.Dispose();
                foreach (var anchor in _anchors.Values) anchor.Retention.Dispose();
                _anchors.Clear();
                foreach (var picker in _pickers.Values) picker.Dispose();
                _pickers.Clear();
                Context.Dispose();
            }
        }

        private interface IContextWorkItem
        {
            ValueTask ExecuteAsync(AutomationContextResource resource);
        }

        private sealed class ContextWorkItem<T>(
            Func<AutomationContextResource, CancellationToken, ValueTask<T>> operation,
            CancellationToken cancellationToken
        ) : IContextWorkItem
        {
            public Task<T> Completion => _completion.Task;

            private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public async ValueTask ExecuteAsync(AutomationContextResource resource)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _completion.TrySetResult(await operation(resource, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _completion.TrySetCanceled(cancellationToken);
                }
                catch (Exception exception)
                {
                    _completion.TrySetException(exception);
                }
            }
        }

        private sealed class ContextStreamWorkItem<T>(
            Func<AutomationContextResource, CancellationToken, IAsyncEnumerable<T>> operation,
            CancellationToken cancellationToken
        ) : IContextWorkItem
        {
            private readonly Channel<T> _items = Channel.CreateBounded<T>(
                new BoundedChannelOptions(4)
                {
                    SingleReader = true,
                    SingleWriter = true,
                    FullMode = BoundedChannelFullMode.Wait,
                    AllowSynchronousContinuations = false,
                });
            private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            public IAsyncEnumerable<T> ReadAllAsync(CancellationToken token) => _items.Reader.ReadAllAsync(token);

            public void Cancel()
            {
                try
                {
                    _cancellation.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Normal completion may dispose the source before the consumer exits its iterator.
                }
            }

            public async ValueTask ExecuteAsync(AutomationContextResource resource)
            {
                var token = _cancellation.Token;
                try
                {
                    token.ThrowIfCancellationRequested();
                    await foreach (var item in operation(resource, token).WithCancellation(token).ConfigureAwait(false))
                    {
                        await _items.Writer.WriteAsync(item, token).ConfigureAwait(false);
                    }

                    _items.Writer.TryComplete();
                }
                catch (OperationCanceledException exception) when (token.IsCancellationRequested)
                {
                    _items.Writer.TryComplete(exception);
                }
                catch (Exception exception)
                {
                    _items.Writer.TryComplete(exception);
                }
                finally
                {
                    _cancellation.Dispose();
                }
            }
        }
    }
}