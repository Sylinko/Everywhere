using System.Buffers.Binary;
using MessagePack;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>Closed set of frame types accepted by the RPC reader.</summary>
public enum RpcFrameKind : ushort
{
    /// <summary>A request carrying an operation ID and correlation ID.</summary>
    Request = 1,

    /// <summary>A response matching a request correlation and operation ID.</summary>
    Response = 2,

    /// <summary>A terminal error matching a request correlation ID.</summary>
    Error = 3,

    /// <summary>A one-way notification with no response correlation.</summary>
    Notification = 4,

    /// <summary>A cancellation targeting an in-flight request or stream.</summary>
    Cancel = 5,

    /// <summary>One bounded item in a streaming response.</summary>
    StreamChunk = 6,

    /// <summary>The terminal frame for a stream.</summary>
    StreamEnd = 7
}

/// <summary>Reserved frame flags. Phase 1 requires this field to remain zero.</summary>
[Flags]
public enum RpcFrameFlags : ushort
{
    /// <summary>No special routing semantics.</summary>
    None = 0
}

/// <summary>Constants shared by every RPC reader and writer.</summary>
public static class RpcProtocolConstants
{
    /// <summary>Little-endian encoding of the ASCII <c>EVRP</c> framing magic.</summary>
    public const uint Magic = 0x50525645; // EVRP in little-endian order.

    /// <summary>Size of the fixed little-endian frame header.</summary>
    public const int HeaderSize = 28;

    /// <summary>Reserved operation ID used for the initial handshake request.</summary>
    public const uint HandshakeOperationId = 1;
}

/// <summary>
/// The fixed 28-byte frame header. Offsets are magic (0), kind (4), flags (6),
/// operation (8), correlation (12), sequence (20), and payload length (24).
/// No runtime struct layout is written directly to the pipe.
/// </summary>
public readonly record struct RpcFrameHeader(
    RpcFrameKind Kind,
    RpcFrameFlags Flags,
    uint OperationId,
    ulong CorrelationId,
    uint Sequence,
    int PayloadLength
)
{
    /// <summary>Serializes the header into a caller-owned buffer.</summary>
    public void Write(Span<byte> destination)
    {
        if (destination.Length < RpcProtocolConstants.HeaderSize)
        {
            throw new ArgumentException("The destination is smaller than the RPC frame header.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt32LittleEndian(destination, RpcProtocolConstants.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)Kind);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)Flags);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], OperationId);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], CorrelationId);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], PayloadLength);
    }

    /// <summary>
    /// Parses and validates a header before any payload buffer is allocated.
    /// </summary>
    public static RpcFrameHeader Read(ReadOnlySpan<byte> source, int maximumPayloadLength)
    {
        if (source.Length < RpcProtocolConstants.HeaderSize)
        {
            throw new RpcProtocolException("The RPC frame header is incomplete.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(source) != RpcProtocolConstants.Magic)
        {
            throw new RpcProtocolException("The RPC frame magic is invalid.");
        }

        var kind = (RpcFrameKind)BinaryPrimitives.ReadUInt16LittleEndian(source[4..]);
        if (!Enum.IsDefined(kind))
        {
            throw new RpcProtocolException($"The RPC frame kind {(ushort)kind} is unsupported.");
        }

        var flags = (RpcFrameFlags)BinaryPrimitives.ReadUInt16LittleEndian(source[6..]);
        if (flags != RpcFrameFlags.None)
        {
            throw new RpcProtocolException("The RPC frame contains unsupported flags.");
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(source[24..]);
        if (payloadLength < 0 || payloadLength > maximumPayloadLength)
        {
            throw new RpcProtocolException($"The RPC frame payload length {payloadLength} is invalid.");
        }

        return new RpcFrameHeader(
            kind,
            flags,
            BinaryPrimitives.ReadUInt32LittleEndian(source[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(source[12..]),
            BinaryPrimitives.ReadUInt32LittleEndian(source[20..]),
            payloadLength);
    }
}

/// <summary>Indicates malformed framing, payload limits, or a protocol state violation.</summary>
public sealed class RpcProtocolException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Represents a closed, typed error returned by the remote RPC peer.</summary>
public sealed class RpcRemoteException(string code, string message) : Exception($"Remote RPC error ({code}): {message}")
{
    /// <summary>Stable machine-readable error category.</summary>
    public string Code { get; } = code;

    /// <summary>Redacted diagnostic text supplied by the remote peer.</summary>
    public string RemoteMessage { get; } = message;
}

/// <summary>
/// Bounded transport settings. These are local policy values; a peer cannot
/// enlarge them through the wire protocol.
/// </summary>
public sealed record RpcConnectionOptions
{
    /// <summary>Whether ordinary frames are rejected until the handshake succeeds.</summary>
    public bool RequireHandshake { get; init; } = true;

    /// <summary>Maximum ordinary frame payload in bytes.</summary>
    public int MaximumFramePayloadBytes { get; init; } = 1024 * 1024;

    /// <summary>Maximum serialized handshake payload in bytes.</summary>
    public int MaximumHandshakePayloadBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum serialized remote-error payload in bytes.</summary>
    public int MaximumErrorPayloadBytes { get; init; } = 64 * 1024;

    /// <summary>Maximum payload of one stream chunk.</summary>
    public int MaximumStreamChunkPayloadBytes { get; init; } = 256 * 1024;

    /// <summary>Maximum total payload retained in outbound queues.</summary>
    public int MaximumQueuedPayloadBytes { get; init; } = 8 * 1024 * 1024;

    /// <summary>Maximum number of queued outbound frames.</summary>
    public int MaximumQueuedFrames { get; init; } = 128;

    /// <summary>Deadline for the handshake after the connection is accepted.</summary>
    public TimeSpan HandshakeTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Deadline for completing a frame after its first byte arrives.</summary>
    public TimeSpan PartialFrameTimeout { get; init; } = TimeSpan.FromSeconds(5);

}

/// <summary>A validated header and its immutable serialized payload.</summary>
public sealed class RpcFrame
{
    /// <summary>Creates a frame from a validated header and payload memory.</summary>
    public RpcFrame(RpcFrameHeader header, ReadOnlyMemory<byte> payload)
    {
        Header = header;
        Payload = payload;
    }

    /// <summary>Validated routing and correlation metadata.</summary>
    public RpcFrameHeader Header { get; }

    /// <summary>Serialized MessagePack or control payload.</summary>
    public ReadOnlyMemory<byte> Payload { get; }
}

/// <summary>
/// Client-to-server identity request. The Host compares the claimed role, build,
/// and desktop session with local policy and checks that the PID is a valid positive
/// process identifier; endpoint ACL/ownership supplies the local transport boundary.
/// </summary>
[MessagePackObject]
public sealed partial class RpcHandshake
{
    /// <summary>Exact <c>AssemblyInformationalVersion</c> of the connecting build.</summary>
    [Key(0)]
    public required string AssemblyInformationalVersion { get; init; }

    /// <summary>Role requested by the connecting process.</summary>
    [Key(1)]
    public required string Role { get; init; }

    /// <summary>PID claimed by the connecting process for diagnostics/cross-checking.</summary>
    [Key(2)]
    public required long ProcessId { get; init; }

    /// <summary>Operating-system desktop/login session, not an RPC nonce.</summary>
    [Key(3)]
    public required string DesktopSessionId { get; init; }
}

/// <summary>
/// Host-to-client handshake result. An accepted response creates the connection
/// lease and carries the Host-generated nonce exactly once.
/// </summary>
[MessagePackObject]
public sealed partial class RpcHandshakeAck
{
    /// <summary>Exact build identity reported by the responding Host.</summary>
    [Key(0)]
    public required string AssemblyInformationalVersion { get; init; }

    /// <summary>Role owned by the responding Host.</summary>
    [Key(1)]
    public required string Role { get; init; }

    /// <summary>PID of the responding Host.</summary>
    [Key(2)]
    public required long ProcessId { get; init; }

    /// <summary>Desktop/login session of the responding Host.</summary>
    [Key(3)]
    public required string DesktopSessionId { get; init; }

    /// <summary>Host-generated connection identifier; present only when accepted.</summary>
    [Key(4)]
    public string? ConnectionNonce { get; init; }

    /// <summary>Whether the peer may route ordinary RPC traffic.</summary>
    [Key(5)]
    public required bool Accepted { get; init; }

    /// <summary>Stable rejection category when <see cref="Accepted"/> is false.</summary>
    [Key(6)]
    public string? RejectionCode { get; init; }
}