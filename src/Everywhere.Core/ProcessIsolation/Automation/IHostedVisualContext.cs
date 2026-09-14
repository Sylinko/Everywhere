using Everywhere.Automation;
using Everywhere.Interop;
using Everywhere.ProcessIsolation.Hosting;
using Everywhere.ProcessIsolation.Roles;
using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Automation;

/// <summary>
/// Main-side lifetime and connection-restoration base for one Host-owned visual Context.
/// </summary>
public interface IHostedVisualContext : IDisposable
{
    /// <summary>Ensures that the current Host Context has an active target-publication turn.</summary>
    ValueTask EnsureTurnAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Completes the previous Host Context turn and begins a new one.</summary>
    ValueTask AdvanceTurnAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Creates one interactive picker in the current Host Context.</summary>
    ValueTask<RemoteVisualPicker> BeginPickerAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Updates one current interactive picker without publishing a target.</summary>
    ValueTask<VisualPickerObservation> UpdatePickerAsync(
        RemoteVisualPicker picker,
        PixelPoint point,
        ScreenSelectionMode mode,
        CancellationToken cancellationToken = default);

    /// <summary>Confirms one current picker and adopts its exact candidate as a pre-publication anchor.</summary>
    ValueTask<RemoteVisualAnchor?> ConfirmPickerAsync(
        RemoteVisualPicker picker,
        VisualPickerObservation observation,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>Acquires one pre-publication visual anchor in the current Host Context.</summary>
    ValueTask<RemoteVisualAnchor?> AcquireAnchorAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>Returns one current scalar observation without retaining or publishing the resolved element.</summary>
    ValueTask<VisualElementSnapshot?> ObserveElementAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default);

    /// <summary>Captures one current published target into an independently owned Main-side buffer.</summary>
    ValueTask<IVisualElementCapture> CaptureTargetAsync(
        int targetId,
        CancellationToken cancellationToken = default);

    /// <summary>Captures one current pre-publication anchor.</summary>
    ValueTask<IVisualElementCapture> CaptureAnchorAsync(
        RemoteVisualAnchor anchor,
        CancellationToken cancellationToken = default);

    /// <summary>Returns a fresh field-selected scalar observation of one current pre-publication anchor.</summary>
    ValueTask<VisualElementSnapshot> GetElementSnapshotAsync(
        RemoteVisualAnchor anchor,
        VisualElementFields requestedFields,
        int maxTextCharacters = 0,
        CancellationToken cancellationToken = default);
}

public abstract class HostedVisualContext<TRemoteContext> : IHostedVisualContext where TRemoteContext : RemoteVisualContext
{
    private protected IHostConnectionSource ConnectionSource { get; }

    private protected abstract string ContextResetNotice { get; }

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Lock _stateGate = new();
    private RpcConnection? _connection;
    private TRemoteContext? _context;
    private bool _isDisposed;

    private protected HostedVisualContext(IHostConnectionSource connectionSource)
    {
        ConnectionSource = connectionSource;
    }

    private protected VisualContextResetException CreateContextResetException(Exception? innerException = null) =>
        new(ContextResetNotice, innerException);

    private protected bool IsCurrentContext(RemoteVisualContext context)
    {
        lock (_stateGate)
        {
            return !_isDisposed && ReferenceEquals(context, _context);
        }
    }

    /// <inheritdoc/>
    public ValueTask EnsureTurnAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(static (context, token) => context.EnsureTurnAsync(token), cancellationToken);

    /// <inheritdoc/>
    public ValueTask AdvanceTurnAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(static (context, token) => context.AdvanceTurnAsync(token), cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<RemoteVisualPicker> BeginPickerAsync(CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await state.Context.BeginPickerAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<VisualPickerObservation> UpdatePickerAsync(
        RemoteVisualPicker picker,
        PixelPoint point,
        ScreenSelectionMode mode,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        EnsurePickerContext(state, picker);
        try
        {
            return await picker.UpdateAsync(point, mode, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<RemoteVisualAnchor?> ConfirmPickerAsync(
        RemoteVisualPicker picker,
        VisualPickerObservation observation,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        EnsurePickerContext(state, picker);
        try
        {
            return await picker.ConfirmAsync(observation, query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<RemoteVisualAnchor?> AcquireAnchorAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await state.Context.AcquireAnchorAsync(locator, resolution, query, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<VisualElementSnapshot?> ObserveElementAsync(
        VisualElementLocator locator,
        VisualElementResolution resolution = VisualElementResolution.Direct,
        VisualElementQueryRequest? query = null,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var anchor = await state.Context.AcquireAnchorAsync(locator, resolution, query, cancellationToken).ConfigureAwait(false);
            return anchor?.Snapshot;
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <inheritdoc/>
    public ValueTask<IVisualElementCapture> CaptureTargetAsync(int targetId, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            (context, token) => context.CaptureTargetAsync(targetId, token),
            requiresCurrentTargets: true,
            cancellationToken);

    /// <inheritdoc/>
    public async ValueTask<IVisualElementCapture> CaptureAnchorAsync(
        RemoteVisualAnchor anchor,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        EnsureAnchorContext(state, anchor);
        try
        {
            return await state.Context.CaptureAnchorAsync(anchor, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<VisualElementSnapshot> GetElementSnapshotAsync(
        RemoteVisualAnchor anchor,
        VisualElementFields requestedFields,
        int maxTextCharacters = 0,
        CancellationToken cancellationToken = default)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        EnsureAnchorContext(state, anchor);
        try
        {
            return await state.Context.GetElementSnapshotAsync(
                anchor,
                requestedFields,
                maxTextCharacters,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    /// <summary>Releases the current remote Context. Child SafeHandles preserve their own connection-scoped cleanup path.</summary>
    public void Dispose()
    {
        GC.SuppressFinalize(this);

        RemoteVisualContext? context;
        lock (_stateGate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            context = _context;
            _context = null;
            _connection = null;
        }

        context?.Dispose();
    }

    private protected async ValueTask<ContextState> GetStateAsync(CancellationToken cancellationToken)
    {
        var connection = await ConnectionSource.GetConnectionAsync(ProcessRole.Automation, cancellationToken).ConfigureAwait(false);
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (ReferenceEquals(connection, _connection) && _context is not null)
            {
                return new ContextState(connection, _context);
            }
        }

        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            connection = await ConnectionSource.GetConnectionAsync(ProcessRole.Automation, cancellationToken).ConfigureAwait(false);
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                if (ReferenceEquals(connection, _connection) && _context is not null)
                {
                    return new ContextState(connection, _context);
                }
            }

            var replacement = await CreateRemoteContextAsync(connection, cancellationToken).ConfigureAwait(false);
            TRemoteContext? previous;
            lock (_stateGate)
            {
                if (_isDisposed)
                {
                    replacement.Dispose();
                    throw new ObjectDisposedException(GetType().Name);
                }

                previous = _context;
                OnRemoteContextReplacing(previous);
                _connection = connection;
                _context = replacement;
            }

            previous?.Dispose();
            return new ContextState(connection, replacement);
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private protected async ValueTask ExecuteAsync(
        Func<TRemoteContext, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await operation(state.Context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    private protected async ValueTask<T> ExecuteAsync<T>(
        Func<TRemoteContext, CancellationToken, ValueTask<T>> operation,
        bool requiresCurrentTargets,
        CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (requiresCurrentTargets) EnsureCurrentTargetState(state.Context);
        try
        {
            return await operation(state.Context, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    private protected async ValueTask<T> ExecutePublishingAsync<T>(
        Func<TRemoteContext, CancellationToken, ValueTask<T>> operation,
        bool requiresCurrentTargets,
        CancellationToken cancellationToken)
    {
        var state = await GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (requiresCurrentTargets) EnsureCurrentTargetState(state.Context);
        try
        {
            var response = await operation(state.Context, cancellationToken).ConfigureAwait(false);
            OnTargetsPublished(state.Context);
            return response;
        }
        catch (Exception exception) when (HandleConnectionFailure(state, exception))
        {
            throw CreateContextResetException(exception);
        }
    }

    private protected bool HandleConnectionFailure(ContextState state, Exception exception)
    {
        if (exception is OperationCanceledException || !state.Connection.Completion.IsCompleted) return false;
        OnVisualContextInvalidated(state.Context);
        return true;
    }

    private protected virtual void OnVisualContextInvalidated(TRemoteContext context)
    {
    }

    private protected abstract ValueTask<TRemoteContext> CreateRemoteContextAsync(RpcConnection connection, CancellationToken cancellationToken);

    private protected virtual void OnRemoteContextReplacing(TRemoteContext? previous)
    {
    }

    private protected virtual void OnTargetsPublished(TRemoteContext context)
    {
    }

    private protected virtual void ValidateTargetState(TRemoteContext context)
    {
    }

    private void EnsureCurrentTargetState(TRemoteContext context)
    {
        lock (_stateGate)
        {
            if (_isDisposed || !ReferenceEquals(context, _context)) throw CreateContextResetException();
            ValidateTargetState(context);
        }
    }

    private void EnsureAnchorContext(ContextState state, RemoteVisualAnchor anchor)
    {
        if (ReferenceEquals(anchor.Context, state.Context)) return;
        throw CreateContextResetException();
    }

    private void EnsurePickerContext(ContextState state, RemoteVisualPicker picker)
    {
        if (ReferenceEquals(picker.Context, state.Context)) return;
        picker.Dispose();
        throw CreateContextResetException();
    }

    private protected sealed record ContextState(RpcConnection Connection, TRemoteContext Context);
}