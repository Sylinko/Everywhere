using System.Threading.Channels;
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
    }
}