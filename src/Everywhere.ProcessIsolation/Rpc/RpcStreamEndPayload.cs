using MessagePack;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>Terminal status encoded by a stream-end frame.</summary>
internal enum RpcStreamEndStatus : byte
{
    /// <summary>The producer completed normally.</summary>
    Completed = 0,

    /// <summary>The caller or peer canceled the stream.</summary>
    Cancelled = 1,

    /// <summary>The producer failed and supplied an error category/message.</summary>
    Failed = 2
}

/// <summary>Internal wire payload carrying the terminal state of a stream.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed partial class RpcStreamEndPayload
{
    /// <summary>Terminal status for the stream.</summary>
    [Key(0)]
    public required RpcStreamEndStatus Status { get; init; }

    /// <summary>Stable error category when <see cref="Status"/> is <see cref="RpcStreamEndStatus.Failed"/>.</summary>
    [Key(1)]
    public string? ErrorCode { get; init; }

    /// <summary>Redacted diagnostic message when the stream failed.</summary>
    [Key(2)]
    public string? ErrorMessage { get; init; }
}