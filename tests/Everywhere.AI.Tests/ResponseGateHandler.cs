namespace Everywhere.AI.Tests;

/// <summary>
/// Observes real transport headers without generating a response. Disposal is an
/// emergency teardown path after a failed cancellation assertion, never its success criterion.
/// </summary>
public sealed class ResponseGateHandler : DelegatingHandler
{
    public Task<HttpResponseMessage> ResponseReceived => _response.Task;

    public Task ContentDisposed => _contentDisposed.Task;

    public Task ResponseReleased => Task.WhenAny(_contentDisposed.Task, _streamDisposed.Task);

    private readonly TaskCompletionSource<HttpResponseMessage> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _contentDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _streamDisposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ResponseGateHandler() : base(new SocketsHttpHandler()) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        response.Content = new ObservedContent(response.Content, _contentDisposed, _streamDisposed);
        _response.TrySetResult(response);
        return response;
    }

    // Wrap real response content without buffering it, changing bytes, or manufacturing errors.
    private sealed class ObservedContent : HttpContent
    {
        private readonly HttpContent _original;
        private readonly TaskCompletionSource _disposed;
        private readonly TaskCompletionSource _streamDisposed;

        public ObservedContent(HttpContent original, TaskCompletionSource disposed, TaskCompletionSource streamDisposed)
        {
            _original = original;
            _disposed = disposed;
            _streamDisposed = streamDisposed;
            foreach (var header in original.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) =>
            _original.CopyToAsync(stream, context);

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken) =>
            _original.CopyToAsync(stream, context, cancellationToken);

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ObservedStream(await _original.ReadAsStreamAsync(), _streamDisposed);

        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            new ObservedStream(await _original.ReadAsStreamAsync(cancellationToken), _streamDisposed);

        protected override Stream CreateContentReadStream(CancellationToken cancellationToken) =>
            new ObservedStream(_original.ReadAsStream(cancellationToken), _streamDisposed);

        protected override bool TryComputeLength(out long length)
        {
            length = _original.Headers.ContentLength ?? 0;
            return _original.Headers.ContentLength.HasValue;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _original.Dispose();
                _disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }
    }

    // OpenAI's pipeline owns/disposes the content stream, while the native connectors
    // dispose HttpResponseMessage. Observe both legitimate ownership boundaries.
    private sealed class ObservedStream(Stream original, TaskCompletionSource disposed) : Stream
    {
        public override bool CanRead => original.CanRead;
        public override bool CanSeek => original.CanSeek;
        public override bool CanWrite => original.CanWrite;
        public override long Length => original.Length;
        public override long Position { get => original.Position; set => original.Position = value; }

        public override void Flush() => original.Flush();
        public override int Read(byte[] buffer, int offset, int count) => original.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => original.Read(buffer);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => original.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => original.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => original.Seek(offset, origin);
        public override void SetLength(long value) => original.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => original.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                original.Dispose();
                disposed.TrySetResult();
            }
            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await original.DisposeAsync();
            disposed.TrySetResult();
            GC.SuppressFinalize(this);
        }
    }
}
