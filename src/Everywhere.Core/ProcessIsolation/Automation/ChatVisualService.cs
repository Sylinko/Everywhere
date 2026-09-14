using Everywhere.Automation;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Performs chat visual operations against caller-owned state and owns the shared draft-acquisition Context.
/// </summary>
public sealed class ChatVisualService : IDisposable
{
    /// <summary>Model-facing notice emitted after a visual Context reset.</summary>
    public const string ResetNotice =
        "The visual Context was reset. IDs from earlier visual Contexts are invalid; re-observe before using them. Prior observations and completed actions remain part of the task history.";

    /// <summary>Gets the connection-restoring Context used by picker and text-selection acquisition.</summary>
    public IHostedVisualContext AcquisitionContext => _acquisitionContext;

    private readonly IHostConnectionSource _connectionSource;
    private readonly SharedAcquisitionContext _acquisitionContext;

    internal ChatVisualService(IHostConnectionSource connectionSource)
    {
        _connectionSource = connectionSource;
        _acquisitionContext = new SharedAcquisitionContext(connectionSource);
    }

    /// <summary>Ensures that the supplied chat state has an active Agent target turn.</summary>
    public ValueTask EnsureTurnAsync(ChatVisualState state, CancellationToken cancellationToken = default) =>
        ExecuteAsync(state, static (context, token) => context.EnsureTurnAsync(token), cancellationToken);

    /// <summary>Completes the previous Agent target turn and begins a new one.</summary>
    public ValueTask AdvanceTurnAsync(ChatVisualState state, CancellationToken cancellationToken = default) =>
        ExecuteAsync(state, static (context, token) => context.AdvanceTurnAsync(token), cancellationToken);

    /// <summary>Acquires a draft-owned Anchor in the shared acquisition Context.</summary>
    public ValueTask<RemoteVisualAnchor?> AcquireAnchorAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default) =>
        _acquisitionContext.AcquireAnchorAsync(locator, resolution, query, cancellationToken);

    /// <summary>Observes a scalar element snapshot through the shared acquisition Context.</summary>
    public ValueTask<VisualElementSnapshot?> ObserveElementAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default) =>
        _acquisitionContext.ObserveElementAsync(locator, resolution, query, cancellationToken);

    /// <summary>Moves a draft Anchor into the supplied chat's Context.</summary>
    public async ValueTask<RemoteVisualAnchor> MoveAnchorAsync(
        ChatVisualState state,
        RemoteVisualAnchor anchor,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        var contextState = await GetContextAsync(state, cancellationToken).ConfigureAwait(false);
        if (ReferenceEquals(anchor.Context, contextState.Context)) return anchor;
        try
        {
            var destination = await anchor.MoveAsync(contextState.Context, query, cancellationToken).ConfigureAwait(false);
            return destination;
        }
        catch (Exception exception) when (HandleConnectionFailure(state, contextState, exception))
        {
            throw new VisualContextResetException(ResetNotice, exception);
        }
    }

    /// <summary>Builds model-facing text from destination-owned Anchors and publishes their Agent IDs.</summary>
    public async ValueTask<VisualContextOperationResult<AutomationVisualQueryResponse>> BuildAnchorsAsync(
        ChatVisualState state,
        IReadOnlyList<RemoteVisualAnchor> anchors,
        VisualContextTraverseDirections directions = VisualContextTraverseDirections.All,
        int maximumNodes = VisualQueryRequest.DefaultLimit,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default)
    {
        var contextState = await GetContextAsync(state, cancellationToken).ConfigureAwait(false);
        if (anchors.Any(anchor => !ReferenceEquals(anchor.Context, contextState.Context)))
        {
            state.Invalidate(contextState.Context);
            throw new VisualContextResetException(ResetNotice);
        }

        try
        {
            var response = await contextState.Context.BuildAnchorsAsync(
                anchors,
                directions,
                maximumNodes,
                targetTokenBudget,
                cancellationToken).ConfigureAwait(false);
            state.PublishTargets(contextState.Context);
            return new VisualContextOperationResult<AutomationVisualQueryResponse>(contextState.Context.Id, response);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, contextState, exception))
        {
            throw new VisualContextResetException(ResetNotice, exception);
        }
    }

    /// <summary>Lists current top-level windows and publishes their Agent target IDs.</summary>
    public ValueTask<VisualContextOperationResult<AutomationVisualQueryResponse>> ListWindowsAsync(
        ChatVisualState state,
        int targetTokenBudget = VisualWindowQuery.DefaultTokenBudget,
        CancellationToken cancellationToken = default) =>
        ExecutePublishingAsync(
            state,
            (context, token) => context.ListWindowsAsync(targetTokenBudget, token),
            requiresCurrentTargets: false,
            cancellationToken);

    /// <summary>Queries one current Agent target.</summary>
    public ValueTask<VisualContextOperationResult<AutomationVisualQueryResponse>> QueryTargetAsync(
        ChatVisualState state,
        int targetId,
        VisualContextTraverseDirections directions = VisualContextTraverseDirections.All,
        int offset = 1,
        int limit = VisualQueryRequest.DefaultLimit,
        int targetTokenBudget = 4096,
        CancellationToken cancellationToken = default) =>
        ExecutePublishingAsync(
            state,
            (context, token) => context.QueryTargetAsync(targetId, directions, offset, limit, targetTokenBudget, token),
            requiresCurrentTargets: true,
            cancellationToken);

    /// <summary>Reads one bounded text page from a current Agent target.</summary>
    public ValueTask<VisualContextOperationResult<string>> ReadTextAsync(
        ChatVisualState state,
        int targetId,
        int offset = 0,
        int limit = VisualQuery.DefaultTextLimit,
        CancellationToken cancellationToken = default) =>
        ExecuteReferencedAsync(
            state,
            (context, token) => context.ReadTextAsync(targetId, offset, limit, token),
            requiresCurrentTargets: true,
            cancellationToken);

    /// <summary>Captures one current Agent target.</summary>
    public ValueTask<VisualContextOperationResult<IVisualElementCapture>> CaptureTargetAsync(
        ChatVisualState state,
        int targetId,
        CancellationToken cancellationToken = default) =>
        ExecuteReferencedAsync(
            state,
            (context, token) => context.CaptureTargetAsync(targetId, token),
            requiresCurrentTargets: true,
            cancellationToken);

    /// <summary>Executes an already-authorized action batch without replaying it after connection loss.</summary>
    public async ValueTask<Guid?> ExecuteActionsAsync(
        ChatVisualState state,
        IReadOnlyList<AutomationActionStep> actions,
        CancellationToken cancellationToken = default)
    {
        var contextState = await GetContextAsync(state, cancellationToken).ConfigureAwait(false);
        var hasTargetReferences = actions.Any(static action => action.TargetId is not null);
        if (hasTargetReferences) state.ValidateTargets(contextState.Context);
        try
        {
            await contextState.Context.ExecuteActionsAsync(actions, cancellationToken).ConfigureAwait(false);
            return hasTargetReferences ? contextState.Context.Id : null;
        }
        catch (Exception exception) when (HandleConnectionFailure(state, contextState, exception))
        {
            throw new VisualActionOutcomeUnknownException(
                $"The Automation Host connection was lost while executing the action batch, so its outcome is unknown. {ResetNotice}",
                exception);
        }
    }

    /// <summary>Releases the shared acquisition Context.</summary>
    public void Dispose() => _acquisitionContext.Dispose();

    internal async ValueTask<Guid> GetCurrentContextIdAsync(ChatVisualState state, CancellationToken cancellationToken) =>
        (await GetContextAsync(state, cancellationToken).ConfigureAwait(false)).Context.Id;

    private async ValueTask<ContextState> GetContextAsync(ChatVisualState state, CancellationToken cancellationToken)
    {
        var connection = await _connectionSource.GetConnectionAsync(ProcessRole.Automation, cancellationToken).ConfigureAwait(false);
        if (state.TryGetContext(connection, out var currentContext)) return new ContextState(connection, currentContext);

        await state.ConnectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection = await _connectionSource.GetConnectionAsync(ProcessRole.Automation, cancellationToken).ConfigureAwait(false);
            if (state.TryGetContext(connection, out currentContext)) return new ContextState(connection, currentContext);

            var replacement = await new AutomationHostClient(connection).CreateContextAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            RemoteVisualContext? previous;
            try
            {
                previous = state.ReplaceContext(connection, replacement);
            }
            catch
            {
                replacement.Dispose();
                throw;
            }

            previous?.Dispose();
            return new ContextState(connection, replacement);
        }
        finally
        {
            state.ConnectionGate.Release();
        }
    }

    private async ValueTask ExecuteAsync(
        ChatVisualState state,
        Func<RemoteVisualContext, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var contextState = await GetContextAsync(state, cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(contextState.Context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, contextState, exception))
        {
            throw new VisualContextResetException(ResetNotice, exception);
        }
    }

    private async ValueTask<VisualContextOperationResult<T>> ExecuteReferencedAsync<T>(
        ChatVisualState state,
        Func<RemoteVisualContext, CancellationToken, ValueTask<T>> operation,
        bool requiresCurrentTargets,
        CancellationToken cancellationToken)
    {
        var contextState = await GetContextAsync(state, cancellationToken).ConfigureAwait(false);
        if (requiresCurrentTargets) state.ValidateTargets(contextState.Context);
        try
        {
            var result = await operation(contextState.Context, cancellationToken).ConfigureAwait(false);
            return new VisualContextOperationResult<T>(contextState.Context.Id, result);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, contextState, exception))
        {
            throw new VisualContextResetException(ResetNotice, exception);
        }
    }

    private async ValueTask<VisualContextOperationResult<T>> ExecutePublishingAsync<T>(
        ChatVisualState state,
        Func<RemoteVisualContext, CancellationToken, ValueTask<T>> operation,
        bool requiresCurrentTargets,
        CancellationToken cancellationToken)
    {
        var contextState = await GetContextAsync(state, cancellationToken).ConfigureAwait(false);
        if (requiresCurrentTargets) state.ValidateTargets(contextState.Context);
        try
        {
            var response = await operation(contextState.Context, cancellationToken).ConfigureAwait(false);
            state.PublishTargets(contextState.Context);
            return new VisualContextOperationResult<T>(contextState.Context.Id, response);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, contextState, exception))
        {
            throw new VisualContextResetException(ResetNotice, exception);
        }
    }

    private static bool HandleConnectionFailure(ChatVisualState state, ContextState contextState, Exception exception)
    {
        if (exception is OperationCanceledException || !contextState.Connection.Completion.IsCompleted) return false;
        state.Invalidate(contextState.Context);
        return true;
    }

    private sealed class SharedAcquisitionContext(IHostConnectionSource connectionSource) : HostedVisualContext<RemoteVisualContext>(connectionSource)
    {
        private protected override string ContextResetNotice => ResetNotice;

        private protected override async ValueTask<RemoteVisualContext> CreateRemoteContextAsync(
            RpcConnection connection,
            CancellationToken cancellationToken) =>
            await new AutomationHostClient(connection).CreateContextAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private sealed record ContextState(RpcConnection Connection, RemoteVisualContext Context);
}

/// <summary>Pairs an operation result with the immutable identity of the visual Context that produced it.</summary>
public readonly record struct VisualContextOperationResult<T>(Guid VisualContextId, T Value);

/// <summary>Indicates that a visual operation addressed a Context that has been reset.</summary>
public sealed class VisualContextResetException : InvalidOperationException
{
    /// <summary>Creates a reset exception.</summary>
    public VisualContextResetException(string message, Exception? innerException = null) : base(message, innerException)
    {
    }
}

/// <summary>Indicates that connection loss made the outcome of a non-replayable visual action unknown.</summary>
public sealed class VisualActionOutcomeUnknownException : InvalidOperationException
{
    /// <summary>Creates an unknown-outcome exception.</summary>
    public VisualActionOutcomeUnknownException(string message, Exception innerException) : base(message, innerException)
    {
    }
}