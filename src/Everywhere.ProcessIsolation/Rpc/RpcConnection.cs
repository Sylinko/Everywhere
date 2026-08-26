using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Everywhere.Utilities;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
///     Typed local RPC connection for Everywhere's fixed client-to-server call model.
///     The public surface composes three private owners: frame transport, operation
///     routing, and correlation lifetime. It does not expose those implementation
///     details as reusable framework APIs.
/// </summary>
public sealed class RpcConnection : IAsyncDisposable
{
    /// <summary>Handles a one-way server-to-client notification.</summary>
    public delegate ValueTask RpcNotificationHandler(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>Handles a request payload and returns its serialized response.</summary>
    public delegate ValueTask<ReadOnlyMemory<byte>> RpcRequestHandler(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>Produces serialized chunks for a streamed response.</summary>
    public delegate IAsyncEnumerable<ReadOnlyMemory<byte>> RpcStreamHandler(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);

    /// <summary>Whether this endpoint serves requests rather than initiating them.</summary>
    public bool IsServer { get; }

    /// <summary>Whether the reader and writer have been started.</summary>
    public bool IsStarted => IsStartedCore;

    /// <summary>Terminal connection signal used by Host lifetime supervision.</summary>
    public Task Completion => _completion.Task;

    /// <summary>Host-generated identity of the authenticated connection lease.</summary>
    public string? ConnectionNonce
    {
        get
        {
            lock (_handshakeGate)
            {
                return _connectionNonce;
            }
        }
    }

    private AtomicBoolean IsDisposed => new(ref _isDisposed);
    private AtomicBoolean IsFailureSignaled => new(ref _isFailureSignaled);
    private AtomicBoolean IsGracefulShutdownRequested => new(ref _isGracefulShutdownRequested);
    private AtomicBoolean IsHandshakeCompleted => new(ref _isHandshakeCompleted);
    private AtomicBoolean IsStartedCore => new(ref _isStarted);

    private readonly MessagePackRpcPayloadCodec _codec;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _disposeGate = new();
    private readonly Lock _handshakeGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OperationRegistry _operations = new();
    private readonly RpcConnectionOptions _options;
    private readonly Router _router = new();
    private readonly FrameTransport _transport;

    private string? _connectionNonce;
    private Task? _disposeTask;
    private int _isDisposed;
    private CancellationTokenRegistration _externalCancellationRegistration;
    private int _isFailureSignaled;
    private int _isGracefulShutdownRequested;
    private int _isHandshakeCompleted;
    private Task? _handshakeTimeoutTask;
    private int _isStarted;

    /// <summary>Creates an unstarted connection over an owned full-duplex stream.</summary>
    public RpcConnection(Stream stream, bool isServer, RpcConnectionOptions? options = null, MessagePackRpcPayloadCodec? codec = null)
    {
        IsServer = isServer;
        _options = options ?? new RpcConnectionOptions();
        _codec = codec ?? new MessagePackRpcPayloadCodec();
        _transport = new FrameTransport(stream, isServer, _options);
    }

    /// <summary>Idempotently stops the connection and disposes its owned stream.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }
    }

    /// <summary>Starts one reader and one writer for this connection.</summary>
    public void Start(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, nameof(RpcConnection));

        if (!IsStartedCore.FlipIfFalse())
        {
            throw new InvalidOperationException("The RPC connection has already been started.");
        }

        if (cancellationToken.CanBeCanceled)
        {
            _externalCancellationRegistration = cancellationToken.Register(_lifetime.Cancel);
        }

        _transport.Start(DispatchAsync, Fail, _lifetime.Token);
        if (_options.RequireHandshake)
        {
            _handshakeTimeoutTask = EnforceHandshakeTimeoutAsync();
        }
    }

    /// <summary>Registers a fixed request operation on the server router.</summary>
    public void RegisterRequestHandler(uint operationId, RpcRequestHandler handler)
    {
        _router.RegisterRequest(operationId, handler);
    }

    /// <summary>Registers a fixed notification operation on the client router.</summary>
    public void RegisterNotificationHandler(uint operationId, RpcNotificationHandler handler)
    {
        _router.RegisterNotification(operationId, handler);
    }

    /// <summary>Registers a typed request handler using this connection's codec.</summary>
    public void RegisterRequestHandler<TRequest, TResponse>(uint operationId, Func<TRequest, CancellationToken, ValueTask<TResponse>> handler)
    {
        RegisterRequestHandler(
            operationId,
            async (payload, cancellationToken) =>
            {
                var request = _codec.Deserialize<TRequest>(payload);
                var response = await handler(request, cancellationToken).ConfigureAwait(false);
                return _codec.Serialize(response);
            });
    }

    /// <summary>Registers a typed server-to-client notification handler.</summary>
    public void RegisterNotificationHandler<TNotification>(uint operationId, Func<TNotification, CancellationToken, ValueTask> handler)
    {
        RegisterNotificationHandler(
            operationId,
            async (payload, cancellationToken) =>
            {
                var notification = _codec.Deserialize<TNotification>(payload);
                await handler(notification, cancellationToken).ConfigureAwait(false);
            });
    }

    /// <summary>Registers a fixed streamed-response operation on the server router.</summary>
    public void RegisterStreamHandler(uint operationId, RpcStreamHandler handler)
    {
        _router.RegisterStream(operationId, handler);
    }

    /// <summary>Registers a typed streamed-response operation.</summary>
    public void RegisterStreamHandler<TRequest, TItem>(uint operationId, Func<TRequest, CancellationToken, IAsyncEnumerable<TItem>> handler)
    {
        RegisterStreamHandler(
            operationId,
            (payload, cancellationToken) => SerializeStreamAsync(
                _codec.Deserialize<TRequest>(payload),
                handler,
                cancellationToken));
    }

    /// <summary>Sends one client request. Product-level retry policy remains outside the transport.</summary>
    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(uint operationId, TRequest request, CancellationToken cancellationToken = default)
    {
        return InvokeCoreAsync<TRequest, TResponse>(operationId, request, cancellationToken);
    }

    /// <summary>
    ///     Enqueues a server-to-client notification. Completion means the local FIFO
    ///     accepted the frame, not that the client callback has completed.
    /// </summary>
    public ValueTask SendNotificationAsync<TNotification>(uint operationId, TNotification notification, CancellationToken cancellationToken = default)
    {
        EnsureNotificationReady();
        return _transport.EnqueueAsync(
            new OutboundFrame(
                RpcFrameKind.Notification,
                operationId,
                0,
                0,
                _codec.Serialize(notification)),
            cancellationToken);
    }

    /// <summary>Invokes a client request whose response is a bounded chunk sequence.</summary>
    public async IAsyncEnumerable<TItem> InvokeStreamAsync<TRequest, TItem>(
        uint operationId,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        EnsureRequestReady(operationId);
        var pending = _operations.AddStream(operationId);

        try
        {
            await _transport.EnqueueAsync(
                new OutboundFrame(
                    RpcFrameKind.Request,
                    operationId,
                    pending.CorrelationId,
                    0,
                    _codec.Serialize(request)),
                cancellationToken).ConfigureAwait(false);

            // Registration follows FIFO acceptance so a cancellation frame cannot
            // overtake the request it targets.
            await using var cancellationRegistration = cancellationToken.Register(() => CancelClientOperation(pending));
            await foreach (var payload in pending.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return _codec.Deserialize<TItem>(payload);
            }
        }
        finally
        {
            if (pending.TryCancel())
            {
                _transport.TryEnqueue(OutboundFrame.Cancel(pending.CorrelationId));
            }

            _operations.RemoveClient(pending.CorrelationId, pending);
        }
    }

    /// <summary>Performs the reserved exact-build handshake and binds its connection nonce.</summary>
    public async ValueTask<RpcHandshakeAck> PerformHandshakeAsync(RpcHandshake handshake, CancellationToken cancellationToken = default)
    {
        using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        handshakeTimeout.CancelAfter(_options.HandshakeTimeout);
        var response = await InvokeAsync<RpcHandshake, RpcHandshakeAck>(
            RpcProtocolConstants.HandshakeOperationId,
            handshake,
            handshakeTimeout.Token).ConfigureAwait(false);

        if (!response.Accepted)
        {
            throw new RpcRemoteException(
                response.RejectionCode ?? "handshake_rejected",
                "The peer rejected the RPC handshake.");
        }

        BindConnectionNonce(response.ConnectionNonce);
        return response;
    }

    /// <summary>
    ///     Closes the server connection after the current response and all FIFO frames
    ///     already accepted before it have been written.
    /// </summary>
    public void RequestGracefulShutdown()
    {
        IsGracefulShutdownRequested.FlipIfFalse();
    }

    private async ValueTask<TResponse> InvokeCoreAsync<TRequest, TResponse>(uint operationId, TRequest request, CancellationToken cancellationToken)
    {
        EnsureRequestReady(operationId);
        var pending = _operations.AddResponse(operationId);

        try
        {
            await _transport.EnqueueAsync(
                new OutboundFrame(
                    RpcFrameKind.Request,
                    operationId,
                    pending.CorrelationId,
                    0,
                    _codec.Serialize(request)),
                cancellationToken).ConfigureAwait(false);

            // Registration follows FIFO acceptance so a cancellation frame cannot
            // overtake the request it targets.
            await using var cancellationRegistration = cancellationToken.Register(() => CancelClientOperation(pending));
            var payload = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return _codec.Deserialize<TResponse>(payload);
        }
        finally
        {
            _operations.RemoveClient(pending.CorrelationId, pending);
        }
    }

    private async ValueTask DispatchAsync(RpcFrame frame, CancellationToken cancellationToken)
    {
        if (_options.RequireHandshake &&
            !IsHandshakeCompleted &&
            frame.Header.Kind is not RpcFrameKind.Request &&
            frame.Header.Kind is not RpcFrameKind.Response &&
            frame.Header.Kind is not RpcFrameKind.Error)
        {
            throw new RpcProtocolException("The RPC handshake must complete before this frame kind is accepted.");
        }

        switch (frame.Header.Kind)
        {
            case RpcFrameKind.Request:
                _router.Track(DispatchRequestAsync(frame, cancellationToken), FailDispatch);
                break;
            case RpcFrameKind.Response:
                DispatchResponse(frame);
                break;
            case RpcFrameKind.Error:
                DispatchError(frame);
                break;
            case RpcFrameKind.Notification:
                DispatchNotification(frame, cancellationToken);
                break;
            case RpcFrameKind.Cancel:
                await _operations.CancelServerAsync(frame.Header.CorrelationId).ConfigureAwait(false);
                break;
            case RpcFrameKind.StreamChunk:
                await DispatchStreamChunkAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
            case RpcFrameKind.StreamEnd:
                await DispatchStreamEndAsync(frame, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask DispatchRequestAsync(RpcFrame frame, CancellationToken cancellationToken)
    {
        if (_options.RequireHandshake
            && !IsHandshakeCompleted
            && frame.Header.OperationId != RpcProtocolConstants.HandshakeOperationId)
        {
            await SendErrorAsync(
                frame.Header.OperationId,
                frame.Header.CorrelationId,
                "handshake_required",
                "The RPC handshake must complete before other operations.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (_router.TryGetStream(frame.Header.OperationId, out var streamHandler))
        {
            await DispatchStreamRequestAsync(frame, streamHandler, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_router.TryGetRequest(frame.Header.OperationId, out var handler))
        {
            throw new RpcProtocolException("The RPC request operation is not registered.");
        }

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operations.AddServer(frame.Header.CorrelationId, requestCancellation))
        {
            throw new RpcProtocolException("The RPC client reused an active correlation ID.");
        }

        try
        {
            var response = await handler(frame.Payload, requestCancellation.Token).ConfigureAwait(false);
            if (frame.Header.OperationId == RpcProtocolConstants.HandshakeOperationId)
            {
                MarkHandshakeFromResponse(response);
            }

            await _transport.EnqueueAsync(
                new OutboundFrame(
                    RpcFrameKind.Response,
                    frame.Header.OperationId,
                    frame.Header.CorrelationId,
                    0,
                    response.ToArray()),
                cancellationToken).ConfigureAwait(false);
            CompleteGracefulShutdownIfRequested();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await SendErrorAsync(
                frame.Header.OperationId,
                frame.Header.CorrelationId,
                "cancelled",
                "The RPC operation was cancelled.",
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The connection is already closing, so there is no peer left to notify.
        }
        catch (Exception exception)
        {
            await SendErrorAsync(
                frame.Header.OperationId,
                frame.Header.CorrelationId,
                "handler_error",
                exception.Message,
                cancellationToken).ConfigureAwait(false);
            CompleteGracefulShutdownIfRequested();
        }
        finally
        {
            _operations.RemoveServer(frame.Header.CorrelationId, requestCancellation);
        }
    }

    private void DispatchResponse(RpcFrame frame)
    {
        if (_operations.TryGetClient(frame.Header.CorrelationId, out var operation))
        {
            operation.ValidateOperation(frame.Header.OperationId);
            if (operation is not PendingResponse response)
            {
                throw new RpcProtocolException("A non-stream response targeted a stream operation.");
            }

            response.TryComplete(frame.Payload);
        }
    }

    private void DispatchError(RpcFrame frame)
    {
        if (!_operations.TryGetClient(frame.Header.CorrelationId, out var operation))
        {
            return;
        }

        operation.ValidateOperation(frame.Header.OperationId);
        var error = _codec.Deserialize<RpcErrorPayload>(frame.Payload);
        operation.Fail(new RpcRemoteException(error.Code, error.Message));
    }

    private void DispatchNotification(RpcFrame frame, CancellationToken cancellationToken)
    {
        if (!_router.TryGetNotification(frame.Header.OperationId, out var handler))
        {
            throw new RpcProtocolException("The RPC notification operation is not registered.");
        }

        _router.Track(handler(frame.Payload, cancellationToken), FailDispatch);
    }

    private async ValueTask DispatchStreamRequestAsync(RpcFrame frame, RpcStreamHandler handler, CancellationToken cancellationToken)
    {
        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_operations.AddServer(frame.Header.CorrelationId, requestCancellation))
        {
            throw new RpcProtocolException("The RPC client reused an active correlation ID.");
        }

        var sequence = 0u;
        var sentChunk = false;
        try
        {
            await foreach (var payload in handler(frame.Payload, requestCancellation.Token).ConfigureAwait(false))
            {
                await _transport.EnqueueAsync(
                    new OutboundFrame(
                        RpcFrameKind.StreamChunk,
                        frame.Header.OperationId,
                        frame.Header.CorrelationId,
                        sequence++,
                        payload.ToArray()),
                    cancellationToken).ConfigureAwait(false);
                sentChunk = true;
            }

            await SendStreamEndAsync(
                frame.Header.OperationId,
                frame.Header.CorrelationId,
                sequence,
                new RpcStreamEndPayload { Status = RpcStreamEndStatus.Completed },
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            if (sentChunk)
            {
                await SendStreamEndAsync(
                    frame.Header.OperationId,
                    frame.Header.CorrelationId,
                    sequence,
                    new RpcStreamEndPayload
                    {
                        Status = RpcStreamEndStatus.Cancelled,
                        ErrorCode = "cancelled",
                        ErrorMessage = "The RPC stream was cancelled.",
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendErrorAsync(
                    frame.Header.OperationId,
                    frame.Header.CorrelationId,
                    "cancelled",
                    "The RPC stream was cancelled.",
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The connection is already closing, so there is no peer left to notify.
        }
        catch (Exception exception)
        {
            if (sentChunk)
            {
                await SendStreamEndAsync(
                    frame.Header.OperationId,
                    frame.Header.CorrelationId,
                    sequence,
                    new RpcStreamEndPayload
                    {
                        Status = RpcStreamEndStatus.Failed,
                        ErrorCode = "stream_error",
                        ErrorMessage = exception.Message,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SendErrorAsync(
                    frame.Header.OperationId,
                    frame.Header.CorrelationId,
                    "stream_error",
                    exception.Message,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _operations.RemoveServer(frame.Header.CorrelationId, requestCancellation);
        }
    }

    private ValueTask DispatchStreamChunkAsync(RpcFrame frame, CancellationToken cancellationToken)
    {
        if (!_operations.TryGetClient(frame.Header.CorrelationId, out var operation))
        {
            return ValueTask.CompletedTask;
        }

        operation.ValidateOperation(frame.Header.OperationId);
        if (operation is not PendingStream stream)
        {
            throw new RpcProtocolException("A stream chunk targeted a non-stream operation.");
        }

        return stream.AddChunkAsync(frame.Header.Sequence, frame.Payload, cancellationToken);
    }

    private ValueTask DispatchStreamEndAsync(RpcFrame frame, CancellationToken cancellationToken)
    {
        if (!_operations.TryGetClient(frame.Header.CorrelationId, out var operation))
        {
            return ValueTask.CompletedTask;
        }

        operation.ValidateOperation(frame.Header.OperationId);
        if (operation is not PendingStream stream)
        {
            throw new RpcProtocolException("A stream terminal frame targeted a non-stream operation.");
        }

        var end = _codec.Deserialize<RpcStreamEndPayload>(frame.Payload);
        return stream.CompleteAsync(frame.Header.Sequence, end, cancellationToken);
    }

    private ValueTask SendErrorAsync(
        uint operationId,
        ulong correlationId,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        return _transport.EnqueueAsync(
            new OutboundFrame(
                RpcFrameKind.Error,
                operationId,
                correlationId,
                0,
                _codec.Serialize(new RpcErrorPayload { Code = code, Message = message })),
            cancellationToken);
    }

    private ValueTask SendStreamEndAsync(
        uint operationId,
        ulong correlationId,
        uint sequence,
        RpcStreamEndPayload payload,
        CancellationToken cancellationToken)
    {
        return _transport.EnqueueAsync(
            new OutboundFrame(
                RpcFrameKind.StreamEnd,
                operationId,
                correlationId,
                sequence,
                _codec.Serialize(payload)),
            cancellationToken);
    }

    private async IAsyncEnumerable<ReadOnlyMemory<byte>> SerializeStreamAsync<TRequest, TItem>(
        TRequest request,
        Func<TRequest, CancellationToken, IAsyncEnumerable<TItem>> handler,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in handler(request, cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return _codec.Serialize(item);
        }
    }

    private void CancelClientOperation(PendingOperation operation)
    {
        if (operation.TryCancel())
        {
            _transport.TryEnqueue(OutboundFrame.Cancel(operation.CorrelationId));
        }
    }

    private async Task EnforceHandshakeTimeoutAsync()
    {
        try
        {
            await Task.Delay(_options.HandshakeTimeout, _lifetime.Token).ConfigureAwait(false);
            if (!IsHandshakeCompleted)
            {
                Fail(new TimeoutException("The RPC handshake did not complete within the configured timeout."));
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
    }

    private void BindConnectionNonce(string? connectionNonce)
    {
        if (!Guid.TryParse(connectionNonce, out var nonce) || nonce == Guid.Empty)
        {
            throw new RpcProtocolException("The accepted RPC handshake did not include a valid connection nonce.");
        }

        lock (_handshakeGate)
        {
            if (_connectionNonce is not null && _connectionNonce != connectionNonce)
            {
                throw new RpcProtocolException("The RPC connection nonce changed after handshake.");
            }

            _connectionNonce = connectionNonce;
            IsHandshakeCompleted.FlipIfFalse();
        }
    }

    private void MarkHandshakeFromResponse(ReadOnlyMemory<byte> response)
    {
        var handshake = _codec.Deserialize<RpcHandshakeAck>(response);
        if (handshake.Accepted)
        {
            BindConnectionNonce(handshake.ConnectionNonce);
        }
    }

    private void CompleteGracefulShutdownIfRequested()
    {
        if (IsGracefulShutdownRequested)
        {
            _transport.Complete();
        }
    }

    private void EnsureRequestReady(uint operationId)
    {
        EnsureStarted();
        if (IsServer)
        {
            throw new InvalidOperationException("The server endpoint does not initiate RPC requests.");
        }

        if (_options.RequireHandshake
            && !IsHandshakeCompleted
            && operationId != RpcProtocolConstants.HandshakeOperationId)
        {
            throw new InvalidOperationException("The RPC handshake must complete before sending requests.");
        }
    }

    private void EnsureNotificationReady()
    {
        EnsureStarted();
        if (!IsServer)
        {
            throw new InvalidOperationException("Only the server endpoint sends RPC notifications.");
        }

        if (_options.RequireHandshake && !IsHandshakeCompleted)
        {
            throw new InvalidOperationException("The RPC handshake must complete before sending notifications.");
        }
    }

    private void EnsureStarted()
    {
        if (!IsStarted)
        {
            throw new InvalidOperationException("The RPC connection has not been started.");
        }
    }

    private void FailDispatch(Exception exception)
    {
        if (exception is not OperationCanceledException || !_lifetime.IsCancellationRequested)
        {
            Fail(exception);
        }
    }

    private void Fail(Exception? exception)
    {
        if (IsDisposed || !IsFailureSignaled.FlipIfFalse())
        {
            return;
        }

        if (exception is not null && !_lifetime.IsCancellationRequested)
        {
            _completion.TrySetException(exception);
        }
        else
        {
            _completion.TrySetResult();
        }

        _lifetime.Cancel();
        _transport.Complete(exception);
        _operations.FailClientOperations(exception ?? new EndOfStreamException("The RPC peer disconnected."));
    }

    private async Task DisposeCoreAsync()
    {
        if (IsStartedCore.FlipIfTrue())
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            _transport.Complete();
            try
            {
                await _transport.Completion.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Completion already carries the terminal transport error.
            }

            await _router.WaitForDispatchesAsync().ConfigureAwait(false);
            if (_handshakeTimeoutTask is not null)
            {
                await _handshakeTimeoutTask.ConfigureAwait(false);
            }
        }

        _completion.TrySetResult();
        IsDisposed.FlipIfFalse();
        await _externalCancellationRegistration.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Owns byte-stream framing and the single bounded FIFO writer.</summary>
    private sealed class FrameTransport(Stream stream, bool isServer, RpcConnectionOptions options) : IAsyncDisposable
    {
        public Task Completion
        {
            get
            {
                if (_readerTask is null || _writerTask is null)
                {
                    return Task.CompletedTask;
                }

                return Task.WhenAll(_readerTask, _writerTask);
            }
        }

        private readonly Channel<OutboundFrame> _outbound = Channel.CreateBounded<OutboundFrame>(
            new BoundedChannelOptions(options.MaximumQueuedFrames)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });

        private int _queuedPayloadBytes;
        private Task? _readerTask;
        private Task? _writerTask;

        public ValueTask DisposeAsync()
        {
            return stream.DisposeAsync();
        }

        public void Start(Func<RpcFrame, CancellationToken, ValueTask> receive, Action<Exception?> ended, CancellationToken cancellationToken)
        {
            _readerTask = ReadLoopAsync(receive, ended, cancellationToken);
            _writerTask = WriteLoopAsync(ended, cancellationToken);
        }

        public async ValueTask EnqueueAsync(OutboundFrame frame, CancellationToken cancellationToken)
        {
            ValidateOutboundPayload(frame);
            var queuedBytes = Interlocked.Add(ref _queuedPayloadBytes, frame.Payload.Length);
            if (queuedBytes > options.MaximumQueuedPayloadBytes)
            {
                Interlocked.Add(ref _queuedPayloadBytes, -frame.Payload.Length);
                throw new RpcProtocolException("The RPC outbound payload queue is full.");
            }

            try
            {
                await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                Interlocked.Add(ref _queuedPayloadBytes, -frame.Payload.Length);
                throw;
            }
        }

        public void TryEnqueue(OutboundFrame frame)
        {
            _outbound.Writer.TryWrite(frame);
        }

        public void Complete(Exception? exception = null)
        {
            _outbound.Writer.TryComplete(exception);
        }

        private async Task ReadLoopAsync(
            Func<RpcFrame, CancellationToken, ValueTask> receive,
            Action<Exception?> ended,
            CancellationToken cancellationToken)
        {
            Exception? exception = null;
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var headerBytes = new byte[RpcProtocolConstants.HeaderSize];
                    if (!await ReadHeaderAsync(headerBytes, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    var header = RpcFrameHeader.Read(headerBytes, options.MaximumFramePayloadBytes);
                    ValidateInboundHeader(header);
                    ValidateInboundPayload(header);
                    var payload = new byte[header.PayloadLength];
                    if (payload.Length != 0)
                    {
                        await ReadExactlyWithTimeoutAsync(payload, cancellationToken).ConfigureAwait(false);
                    }

                    await receive(new RpcFrame(header, payload), cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (EndOfStreamException)
            {
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                ended(exception);
            }
        }

        private async Task WriteLoopAsync(Action<Exception?> ended, CancellationToken cancellationToken)
        {
            Exception? exception = null;
            try
            {
                await foreach (var frame in _outbound.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    var headerBytes = new byte[RpcProtocolConstants.HeaderSize];
                    frame.Header.Write(headerBytes);
                    await stream.WriteAsync(headerBytes, cancellationToken).ConfigureAwait(false);
                    if (frame.Payload.Length != 0)
                    {
                        await stream.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                    }

                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                    Interlocked.Add(ref _queuedPayloadBytes, -frame.Payload.Length);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception caught)
            {
                exception = caught;
            }
            finally
            {
                ended(exception);
            }
        }

        private async ValueTask<bool> ReadHeaderAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            var read = await stream.ReadAsync(buffer[..1], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            await ReadExactlyWithTimeoutAsync(buffer[1..], cancellationToken).ConfigureAwait(false);
            return true;
        }

        private async ValueTask ReadExactlyWithTimeoutAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.PartialFrameTimeout);
            await stream.ReadExactlyAsync(buffer, timeout.Token).ConfigureAwait(false);
        }

        private void ValidateInboundHeader(RpcFrameHeader header)
        {
            if (header.Flags != RpcFrameFlags.None)
            {
                throw new RpcProtocolException("RPC frame flags are reserved and must be zero.");
            }

            switch (header.Kind)
            {
                case RpcFrameKind.Request when !isServer:
                case RpcFrameKind.Cancel when !isServer:
                    throw new RpcProtocolException("The RPC client received a client-to-server frame.");
                case RpcFrameKind.Response when isServer:
                case RpcFrameKind.Error when isServer:
                case RpcFrameKind.Notification when isServer:
                case RpcFrameKind.StreamChunk when isServer:
                case RpcFrameKind.StreamEnd when isServer:
                    throw new RpcProtocolException("The RPC server received a server-to-client frame.");
            }

            if (header.Kind is RpcFrameKind.Request or RpcFrameKind.Response or
                RpcFrameKind.Error or RpcFrameKind.StreamChunk or RpcFrameKind.StreamEnd)
            {
                if (header.OperationId == 0 || header.CorrelationId == 0)
                {
                    throw new RpcProtocolException("The RPC operation and correlation IDs must be non-zero.");
                }
            }

            if (header.Kind == RpcFrameKind.Notification && (header.OperationId == 0 || header.CorrelationId != 0))
            {
                throw new RpcProtocolException("The RPC notification header is invalid.");
            }

            if (header.Kind == RpcFrameKind.Cancel && (header.OperationId != 0 || header.CorrelationId == 0 || header.PayloadLength != 0))
            {
                throw new RpcProtocolException("The RPC cancellation header is invalid.");
            }

            if (header.Kind is not RpcFrameKind.StreamChunk and not RpcFrameKind.StreamEnd && header.Sequence != 0)
            {
                throw new RpcProtocolException("Only stream frames may carry a sequence number.");
            }
        }

        private void ValidateInboundPayload(RpcFrameHeader header)
        {
            var maximum = header.OperationId == RpcProtocolConstants.HandshakeOperationId ?
                options.MaximumHandshakePayloadBytes :
                header.Kind switch
                {
                    RpcFrameKind.Error => options.MaximumErrorPayloadBytes,
                    RpcFrameKind.StreamChunk => options.MaximumStreamChunkPayloadBytes,
                    _ => options.MaximumFramePayloadBytes,
                };
            if (header.PayloadLength > maximum)
            {
                throw new RpcProtocolException($"The payload length exceeds the limit for {header.Kind}.");
            }
        }

        private void ValidateOutboundPayload(OutboundFrame frame)
        {
            var maximum = frame.OperationId == RpcProtocolConstants.HandshakeOperationId ?
                options.MaximumHandshakePayloadBytes :
                frame.Kind switch
                {
                    RpcFrameKind.Error => options.MaximumErrorPayloadBytes,
                    RpcFrameKind.StreamChunk => options.MaximumStreamChunkPayloadBytes,
                    _ => options.MaximumFramePayloadBytes,
                };
            if (frame.Payload.Length > maximum)
            {
                throw new RpcProtocolException($"The payload length exceeds the limit for {frame.Kind}.");
            }
        }
    }

    /// <summary>Owns fixed operation routing and active handler dispatches.</summary>
    private sealed class Router
    {
        private readonly Lock _dispatchGate = new();
        private readonly Lock _notificationGate = new();
        private readonly HashSet<Task> _dispatches = [];
        private readonly Dictionary<uint, RpcNotificationHandler> _notifications = [];
        // Request and stream contracts are bound before Start. Main-side product
        // proxies bind their notification contracts after the coordinator has
        // authenticated and published a connection, so notification registration
        // has one small synchronization boundary with reader dispatch.
        private readonly Dictionary<uint, RpcRequestHandler> _requests = [];
        private readonly Dictionary<uint, RpcStreamHandler> _streams = [];

        public void RegisterRequest(uint operationId, RpcRequestHandler handler)
        {
            _requests.Add(operationId, handler);
        }

        public void RegisterNotification(uint operationId, RpcNotificationHandler handler)
        {
            lock (_notificationGate)
            {
                _notifications.Add(operationId, handler);
            }
        }

        public void RegisterStream(uint operationId, RpcStreamHandler handler)
        {
            _streams.Add(operationId, handler);
        }

        public bool TryGetRequest(uint operationId, [NotNullWhen(true)] out RpcRequestHandler? handler)
        {
            return _requests.TryGetValue(operationId, out handler);
        }

        public bool TryGetNotification(uint operationId, [NotNullWhen(true)] out RpcNotificationHandler? handler)
        {
            lock (_notificationGate)
            {
                return _notifications.TryGetValue(operationId, out handler);
            }
        }

        public bool TryGetStream(uint operationId, [NotNullWhen(true)] out RpcStreamHandler? handler)
        {
            return _streams.TryGetValue(operationId, out handler);
        }

        public void Track(ValueTask dispatch, Action<Exception> failed)
        {
            // Handlers run outside the reader so a slow operation cannot prevent its
            // cancellation frame from being consumed.
            var tracked = ObserveAsync(dispatch.AsTask(), failed);
            lock (_dispatchGate)
            {
                _dispatches.Add(tracked);
            }

            tracked.GetAwaiter().OnCompleted(() => RemoveDispatch(tracked));
        }

        public async Task WaitForDispatchesAsync()
        {
            while (true)
            {
                Task[] pending;
                lock (_dispatchGate)
                {
                    _dispatches.RemoveWhere(static task => task.IsCompleted);
                    if (_dispatches.Count == 0)
                    {
                        return;
                    }

                    pending = [.. _dispatches];
                }

                await Task.WhenAll(pending).ConfigureAwait(false);
            }
        }

        private async static Task ObserveAsync(Task dispatch, Action<Exception> failed)
        {
            try
            {
                await dispatch.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failed(exception);
            }
        }

        private void RemoveDispatch(Task dispatch)
        {
            lock (_dispatchGate)
            {
                _dispatches.Remove(dispatch);
            }
        }
    }

    /// <summary>Owns correlation IDs and both sides' cancellable operation state.</summary>
    private sealed class OperationRegistry
    {
        // Client completion, reader dispatch, cancellation, and disposal may touch
        // operation lifetime concurrently, so these two maps share one small lock.
        private readonly Dictionary<ulong, PendingOperation> _client = [];
        private readonly Lock _gate = new();
        private readonly Dictionary<ulong, CancellationTokenSource> _server = [];
        private ulong _nextCorrelationId;

        public PendingResponse AddResponse(uint operationId)
        {
            lock (_gate)
            {
                var pending = new PendingResponse(operationId, NextCorrelationId());
                _client.Add(pending.CorrelationId, pending);
                return pending;
            }
        }

        public PendingStream AddStream(uint operationId)
        {
            lock (_gate)
            {
                var pending = new PendingStream(operationId, NextCorrelationId());
                _client.Add(pending.CorrelationId, pending);
                return pending;
            }
        }

        public bool TryGetClient(ulong correlationId, [NotNullWhen(true)] out PendingOperation? operation)
        {
            lock (_gate)
            {
                return _client.TryGetValue(correlationId, out operation);
            }
        }

        public void RemoveClient(ulong correlationId, PendingOperation operation)
        {
            lock (_gate)
            {
                if (_client.TryGetValue(correlationId, out var current) && ReferenceEquals(current, operation))
                {
                    _client.Remove(correlationId);
                }
            }
        }

        public bool AddServer(ulong correlationId, CancellationTokenSource cancellation)
        {
            lock (_gate)
            {
                return _server.TryAdd(correlationId, cancellation);
            }
        }

        public async ValueTask CancelServerAsync(ulong correlationId)
        {
            CancellationTokenSource? cancellation;
            lock (_gate)
            {
                _server.TryGetValue(correlationId, out cancellation);
            }

            if (cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }
        }

        public void RemoveServer(ulong correlationId, CancellationTokenSource cancellation)
        {
            lock (_gate)
            {
                if (_server.TryGetValue(correlationId, out var current) && ReferenceEquals(current, cancellation))
                {
                    _server.Remove(correlationId);
                }
            }
        }

        public void FailClientOperations(Exception exception)
        {
            PendingOperation[] pending;
            lock (_gate)
            {
                pending = [.. _client.Values];
                _client.Clear();
            }

            foreach (var operation in pending)
            {
                operation.Fail(exception);
            }
        }

        private ulong NextCorrelationId()
        {
            do
            {
                _nextCorrelationId++;
            }
            while (_nextCorrelationId == 0);

            return _nextCorrelationId;
        }
    }

    private abstract class PendingOperation(uint operationId, ulong correlationId)
    {
        public uint OperationId { get; } = operationId;

        public ulong CorrelationId { get; } = correlationId;

        public void ValidateOperation(uint operationId)
        {
            if (operationId != OperationId)
            {
                throw new RpcProtocolException("The RPC completion operation ID does not match its request.");
            }
        }

        public abstract bool TryCancel();

        public abstract void Fail(Exception exception);
    }

    private sealed class PendingResponse(uint operationId, ulong correlationId) : PendingOperation(operationId, correlationId)
    {
        public Task<ReadOnlyMemory<byte>> Task => _source.Task;

        private readonly TaskCompletionSource<ReadOnlyMemory<byte>> _source = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void TryComplete(ReadOnlyMemory<byte> payload)
        {
            _source.TrySetResult(payload);
        }

        public override bool TryCancel()
        {
            return _source.TrySetCanceled();
        }

        public override void Fail(Exception exception)
        {
            _source.TrySetException(exception);
        }
    }

    private sealed class PendingStream(uint operationId, ulong correlationId) : PendingOperation(operationId, correlationId)
    {
        private AtomicBoolean Closed => new(ref _closed);

        private readonly Channel<StreamPart> _parts = Channel.CreateBounded<StreamPart>(
            new BoundedChannelOptions(8)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false,
            });
        private int _closed;
        private uint _nextSequence;

        public async ValueTask AddChunkAsync(uint sequence, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
        {
            if (Closed)
            {
                return;
            }

            ValidateSequence(sequence);
            await _parts.Writer.WriteAsync(new StreamPart(payload, null, false), cancellationToken).ConfigureAwait(false);
        }

        public async ValueTask CompleteAsync(uint sequence, RpcStreamEndPayload end, CancellationToken cancellationToken)
        {
            if (Closed)
            {
                return;
            }

            ValidateSequence(sequence);
            Exception? exception = end.Status switch
            {
                RpcStreamEndStatus.Completed => null,
                RpcStreamEndStatus.Cancelled => new OperationCanceledException(end.ErrorMessage),
                RpcStreamEndStatus.Failed => new RpcRemoteException(
                    end.ErrorCode ?? "stream_error",
                    end.ErrorMessage ?? "The remote RPC stream failed."),
                _ => new RpcProtocolException("The RPC stream end status is invalid."),
            };
            await _parts.Writer.WriteAsync(new StreamPart(default, exception, true), cancellationToken).ConfigureAwait(false);
            Closed.FlipIfFalse();
            _parts.Writer.TryComplete();
        }

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var part in _parts.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!part.IsEnd)
                {
                    yield return part.Payload;
                    continue;
                }

                if (part.Exception is not null)
                {
                    throw part.Exception;
                }

                yield break;
            }
        }

        public override bool TryCancel()
        {
            if (!Closed.FlipIfFalse())
            {
                return false;
            }

            _parts.Writer.TryComplete(new OperationCanceledException());
            return true;
        }

        public override void Fail(Exception exception)
        {
            if (Closed.FlipIfFalse())
            {
                _parts.Writer.TryComplete(exception);
            }
        }

        private void ValidateSequence(uint sequence)
        {
            if (sequence != _nextSequence)
            {
                throw new RpcProtocolException($"The RPC stream sequence {sequence} was expected to be {_nextSequence}.");
            }

            _nextSequence++;
        }
    }

    private readonly record struct StreamPart(
        ReadOnlyMemory<byte> Payload,
        Exception? Exception,
        bool IsEnd
    );

    private readonly record struct OutboundFrame(
        RpcFrameKind Kind,
        uint OperationId,
        ulong CorrelationId,
        uint Sequence,
        byte[] Payload
    )
    {
        public RpcFrameHeader Header => new(
            Kind,
            RpcFrameFlags.None,
            OperationId,
            CorrelationId,
            Sequence,
            Payload.Length);

        public static OutboundFrame Cancel(ulong correlationId)
        {
            return new OutboundFrame(RpcFrameKind.Cancel, 0, correlationId, 0, []);
        }
    }
}