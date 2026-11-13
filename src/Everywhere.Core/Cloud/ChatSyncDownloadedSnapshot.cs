using System.Buffers.Binary;
using System.Text;
using Everywhere.Common;
using MessagePack;
using Serilog;

namespace Everywhere.Cloud;

/// <summary>
/// A complete fixed snapshot staged outside the chat database. Interrupted downloads
/// resume from the last staged frame within this task; incomplete snapshots are never installed.
/// </summary>
public sealed class ChatSyncDownloadedSnapshot : IDisposable
{
    /// <summary>
    /// Gets the coherent target head of this transfer.
    /// </summary>
    public ChatSyncHead Head { get; }

    /// <summary>The installed revision requested by the client. Zero requests the whole copy.</summary>
    public long SinceRevision { get; }

    /// <summary>
    /// The server rebuilt this content since our installed baseline. The fixed snapshot
    /// therefore enumerates the whole new copy, even when discovery preceded the rebuild.
    /// </summary>
    public bool CrossesRepublish => Head.Availability == ChatSyncAvailability.Available && Head.LastRepublishedRevision > SinceRevision;

    private static readonly MessagePackSerializerOptions WireOptions =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly FileStream _stream;

    private ChatSyncDownloadedSnapshot(ChatSyncHead head, long since)
    {
        Head = head;
        SinceRevision = since;
        _stream = new FileStream(
            Path.Combine(RuntimeConstants.EnsureCacheFolderPath("chat-sync"), $"{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            65536,
            FileOptions.DeleteOnClose);
    }

    /// <summary>
    /// Downloads one snapshot, retaining at most one complete node body in memory.
    /// Network waits hold neither a local transaction nor the local commit gate.
    /// </summary>
    public static async Task<ChatSyncDownloadedSnapshot> ReadAsync(
        ChatSyncTransport transport,
        Guid chatId,
        long since,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadAsync(transport, chatId, since, cancellationToken).ConfigureAwait(false);
        }
        catch (ChatSyncException error) when (error.Failure.Code == ChatSyncFailureCode.TransferNotFound)
        {
            // An expired snapshot cannot supply the suffix of a different frozen history.
            cancellationToken.ThrowIfCancellationRequested();
            return await DownloadAsync(transport, chatId, since, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<ChatSyncDownloadedSnapshot> DownloadAsync(ChatSyncTransport transport, Guid chatId, long since, CancellationToken token)
    {
        var initial = await transport.OpenSnapshotAsync(chatId, since, token).ConfigureAwait(false);
        HttpResponseMessage? resumed = null;
        ChatSyncDownloadedSnapshot? result = null;
        Guid? frozenId = null;
        try
        {
            var stream = await initial.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            var snapshot = await ReadFrameAsync<ChatSyncSnapshot>(stream, token).ConfigureAwait(false);
            if (snapshot?.Head is null || !Guid.TryParseExact(snapshot.SnapshotId, "D", out var id) ||
                !Guid.TryParseExact(snapshot.Head.ChatId, "D", out var receivedId) || receivedId != chatId)
            {
                throw new InvalidDataException("Invalid chat snapshot header.");
            }

            frozenId = id;
            result = new ChatSyncDownloadedSnapshot(snapshot.Head, since);
            using var body = new MemoryStream();
            await using var writer = new BinaryWriter(result._stream, Encoding.UTF8, leaveOpen: true);
            var position = 1;
            var reconnects = 0;
            var needsResume = false;
            while (true)
            {
                ChatSyncSnapshotPart? part;
                try
                {
                    if (needsResume)
                    {
                        resumed = await transport.ResumeSnapshotAsync(id, position, body.Length, token).ConfigureAwait(false);
                        stream = await resumed.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                        needsResume = false;
                    }
                    part = await ReadFrameAsync<ChatSyncSnapshotPart?>(stream, token).ConfigureAwait(false);
                }
                catch (Exception error) when (
                    !token.IsCancellationRequested && reconnects < 2 &&
                    error is IOException or HttpRequestException { StatusCode: null } or TimeoutException or OperationCanceledException)
                {
                    // Only the network read is retried. Local staging failures must escape.
                    (resumed ?? initial).Dispose();
                    resumed = null;
                    needsResume = true;
                    reconnects++;
                    await Task.Delay(TimeSpan.FromMilliseconds(250 * reconnects), token).ConfigureAwait(false);
                    continue;
                }

                if (part is null)
                {
                    if (body.Length != 0)
                    {
                        throw new InvalidDataException("The snapshot ended inside a node body.");
                    }

                    token.ThrowIfCancellationRequested();
                    return result;
                }

                if (part.Header is null || part.Body is null || part is { IsComplete: false, Body.Length: 0 })
                {
                    throw new InvalidDataException("Invalid chat snapshot fragment.");
                }

                body.Write(part.Body);
                if (!part.IsComplete)
                {
                    continue;
                }

                var entry = MessagePackSerializer.Serialize(part with { Body = body.ToArray() }, cancellationToken: token);
                writer.Write(entry.Length);
                writer.Write(entry);
                body.SetLength(0);
                position++;
            }
        }
        catch
        {
            result?.Dispose();
            throw;
        }
        finally
        {
            resumed?.Dispose();
            initial.Dispose();
            if (frozenId is { } id && !token.IsCancellationRequested)
            {
                try
                {
                    await transport.ReleaseSnapshotAsync(id, token).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    Log.ForContext<ChatSyncDownloadedSnapshot>().Debug(error, "Snapshot cleanup was interrupted; server expiration will release it.");
                }
            }
        }
    }

    private static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        var prefix = new byte[sizeof(int)];
        await FillAsync(prefix).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(prefix);
        if (length is <= 0 or > ChatSyncProtocol.MaximumPartBytes + 65536)
        {
            throw new InvalidDataException("Invalid chat snapshot frame length.");
        }

        var payload = new byte[length];
        await FillAsync(payload).ConfigureAwait(false);
        return MessagePackSerializer.Deserialize<T?>(payload, WireOptions, token);

        async Task FillAsync(Memory<byte> remaining)
        {
            // Slow links are fine while bytes keep arriving. Never advance a cursor on a partial frame.
            while (!remaining.IsEmpty)
            {
                // ReSharper disable once AccessToDisposedClosure
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                try
                {
                    // ReSharper disable once AccessToDisposedClosure
                    var read = await stream.ReadAsync(remaining, timeout.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        throw new EndOfStreamException("The snapshot stream ended before its final frame.");
                    }
                    remaining = remaining[read..];
                }
                catch (OperationCanceledException error) when (!token.IsCancellationRequested)
                {
                    throw new TimeoutException("The chat snapshot stream stopped making progress.", error);
                }
            }
        }
    }

    internal IEnumerable<ChatSyncSnapshotPart> ReadEntries(CancellationToken cancellationToken)
    {
        _stream.Position = 0;
        using var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        while (_stream.Position < _stream.Length)
        {
            yield return MessagePackSerializer.Deserialize<ChatSyncSnapshotPart>(
                reader.ReadBytes(reader.ReadInt32()),
                cancellationToken: cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose() => _stream.Dispose();
}