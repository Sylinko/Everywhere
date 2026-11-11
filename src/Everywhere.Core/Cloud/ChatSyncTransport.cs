using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using MessagePack;

namespace Everywhere.Cloud;

/// <summary>
/// Account-bound v2 transport. The caller owns the authenticated HttpClient, whose BaseAddress is the cloud service origin.
/// No automatic retry or account enrollment occurs here.
/// </summary>
public sealed class ChatSyncTransport(HttpClient httpClient, string accountId, ChatSyncCompatibility compatibility)
{
    /// <summary>
    /// Account that owns every request, even if the authentication handler refreshes or switches credentials.
    /// </summary>
    public string AccountId { get; } = accountId;

    /// <summary>
    /// Compatibility of this account session. Reconciliation creates a new session rather than mutating it in flight.
    /// </summary>
    public ChatSyncCompatibility Compatibility { get; } = compatibility;

    private static readonly MessagePackSerializerOptions WireOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    /// <summary>
    /// Starts or resumes the exact persisted header, returning a receipt when already committed.
    /// </summary>
    public Task<ChatSyncCommitResult?> BeginAsync(byte[] header, CancellationToken cancellationToken)
    {
        return SendAsync<ChatSyncCommitResult?>(HttpMethod.Post, "uploads/begin", header, cancellationToken);
    }

    /// <summary>
    /// Publishes a complete staged submission; a lost response must be reconciled with its original identity.
    /// </summary>
    public Task<ChatSyncCommitResult> CommitAsync(Guid commitId, int partCount, CancellationToken cancellationToken)
    {
        var body = MessagePackSerializer.Serialize(new Dictionary<string, int> { ["partCount"] = partCount }, cancellationToken: cancellationToken);
        return SendAsync<ChatSyncCommitResult>(HttpMethod.Post, $"uploads/{commitId:D}/commit", body, cancellationToken);
    }

    /// <summary>
    /// Discovers one bounded page; the coordinator persists its work before advancing the cursor.
    /// </summary>
    public Task<ChatSyncDiscovery> DiscoverAsync(long after, CancellationToken cancellationToken)
    {
        return SendAsync<ChatSyncDiscovery>(HttpMethod.Get, $"heads?after={after.ToString(CultureInfo.InvariantCulture)}", null, cancellationToken);
    }

    /// <summary>
    /// Releases a consumed or abandoned download snapshot. Expiration handles interrupted cleanup.
    /// </summary>
    public async Task ReleaseSnapshotAsync(Guid snapshotId, CancellationToken cancellationToken)
    {
        await SendAsync<object?>(HttpMethod.Delete, $"snapshots/{snapshotId:D}", null, cancellationToken);
    }

    /// <summary>
    /// Discards staging only after a definitive rejection. Successful receipts are retained.
    /// </summary>
    public async Task DiscardUploadAsync(Guid commitId, CancellationToken cancellationToken)
    {
        await SendAsync<object?>(HttpMethod.Delete, $"uploads/{commitId:D}", null, cancellationToken);
    }

    /// <summary>Lists cloud copies without reading message bodies or advancing discovery.</summary>
    public Task<ChatSyncStoragePage> ListCopiesAsync(string? cursor, CancellationToken cancellationToken)
    {
        var path = cursor is null ? "storage" : $"storage?cursor={Uri.EscapeDataString(cursor)}";
        return SendAsync<ChatSyncStoragePage>(HttpMethod.Get, path, null, cancellationToken);
    }

    /// <summary>
    /// Reads the first part not durably staged by the server. An interrupted batch may be resent.
    /// </summary>
    public Task<ChatSyncUploadProgress> ReadUploadProgressAsync(Guid commitId, CancellationToken cancellationToken)
    {
        return SendAsync<ChatSyncUploadProgress>(HttpMethod.Get, $"uploads/{commitId:D}/progress", null, cancellationToken);
    }

    /// <summary>
    /// Sends exact frozen parts as length-prefixed MessagePack frames in one HTTP request.
    /// Only one part is materialized at a time; the server commits bounded batches independently.
    /// </summary>
    public async Task UploadPartsAsync(
        Guid commitId,
        int position,
        IAsyncEnumerable<byte[]> parts,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Put,
            $"uploads/{commitId:D}/stream?position={position.ToString(CultureInfo.InvariantCulture)}",
            "application/msgpack");
        request.Content = new UploadContent(parts);
        using var response = await SendHttpAsync(request, cancellationToken).ConfigureAwait(false);
        await ReadMessageAsync<object?>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Stops the confirmed copy using the current user's authenticated session.</summary>
    public Task<ChatSyncCommitResult> StopAsync(Guid chatId, ChatSyncStopConfirmation confirmation, CancellationToken cancellationToken)
    {
        var body = MessagePackSerializer.Serialize(confirmation, cancellationToken: cancellationToken);
        return SendAsync<ChatSyncCommitResult>(HttpMethod.Post, $"storage/chats/{chatId:D}/stop", body, cancellationToken);
    }

    internal Task<HttpResponseMessage> OpenSnapshotAsync(Guid chatId, long since, CancellationToken token)
    {
        var path = FormattableString.Invariant($"chats/{chatId:D}/snapshot/stream?since={since}");
        return OpenStreamAsync(HttpMethod.Post, path, token);
    }

    internal Task<HttpResponseMessage> ResumeSnapshotAsync(Guid snapshotId, int position, long offset, CancellationToken token)
    {
        var path = FormattableString.Invariant($"snapshots/{snapshotId:D}/stream?position={position}&offset={offset}");
        return OpenStreamAsync(HttpMethod.Get, path, token);
    }

    private async Task<HttpResponseMessage> OpenStreamAsync(HttpMethod method, string path, CancellationToken token)
    {
        const string mediaType = "application/vnd.everywhere.snapshot-stream";
        using var request = CreateRequest(method, path, mediaType);
        var response = await SendHttpAsync(request, token).ConfigureAwait(false);
        try
        {
            if (!response.IsSuccessStatusCode)
            {
                await ReadMessageAsync<object?>(response, token).ConfigureAwait(false);
            }
            if (response.Content.Headers.ContentType?.MediaType != mediaType)
            {
                throw new InvalidDataException("Expected a framed chat snapshot.");
            }
            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, string mediaType)
    {
        var request = new HttpRequestMessage(method, "sync/chat-db/v2/" + path);
        request.Headers.Add("ew-sync-account", AccountId);
        request.Headers.Add("ew-sync-protocol", Compatibility.ProtocolVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("ew-sync-generation", Compatibility.SyncGeneration.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("ew-sync-data-generation", Compatibility.DataGeneration.ToString(CultureInfo.InvariantCulture));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(mediaType));
        return request;
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, byte[]? body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path, "application/msgpack");
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/msgpack");
        }

        using var response = await SendHttpAsync(request, cancellationToken).ConfigureAwait(false);
        return await ReadMessageAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendHttpAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (TaskCanceledException error) when (!cancellationToken.IsCancellationRequested && error.InnerException is TimeoutException)
        {
            // A request deadline is one chat's failure, not cancellation of the account pass.
            throw new TimeoutException("Timed out sending chat sync data or waiting for response headers.", error);
        }
    }

    private static async Task<T> ReadMessageAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers. Control bodies need
        // their own bounded read deadline; snapshot streams use a per-read idle timeout instead.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await response.Content.LoadIntoBufferAsync(ChatSyncProtocol.MaximumPartBytes + 64 * 1024, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Timed out reading a chat sync control response.", error);
        }

        if (response.Content.Headers.ContentType?.MediaType != "application/msgpack")
        {
            response.EnsureSuccessStatusCode();
            throw new InvalidDataException("The sync response is not MessagePack.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var failure = MessagePackSerializer.Deserialize<ChatSyncFailure>(bytes, WireOptions, cancellationToken);
            throw new ChatSyncException(failure);
        }

        return MessagePackSerializer.Deserialize<T>(bytes, WireOptions, cancellationToken);
    }

    private sealed class UploadContent : HttpContent
    {
        private readonly IAsyncEnumerable<byte[]> _parts;

        public UploadContent(IAsyncEnumerable<byte[]> parts)
        {
            _parts = parts;
            Headers.ContentType = new MediaTypeHeaderValue("application/vnd.everywhere.upload-stream");
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return SerializeToStreamAsync(stream, context, CancellationToken.None);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var prefix = new byte[sizeof(int)];
            await foreach (var part in _parts.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (part.Length is <= 0 or > ChatSyncProtocol.MaximumPartBytes)
                {
                    throw new InvalidDataException("The frozen upload part exceeds the wire limit.");
                }

                BinaryPrimitives.WriteInt32BigEndian(prefix, part.Length);
                await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
                await stream.WriteAsync(part, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>
/// A structured sync rejection. The coordinator decides whether to reconcile, block, or retry;
/// user-facing text is localized separately from these protocol codes and quota details.
/// </summary>
public sealed class ChatSyncException(ChatSyncFailure failure) : Exception(failure.RawCode)
{
    /// <summary>
    /// Server rejection, including numerical quota details when present.
    /// </summary>
    public ChatSyncFailure Failure { get; } = failure;

    /// <summary>
    /// Rejects a local operation with the same typed error used by the wire protocol.
    /// </summary>
    public ChatSyncException(ChatSyncFailureCode code) : this(new ChatSyncFailure(code, []))
    {
    }
}