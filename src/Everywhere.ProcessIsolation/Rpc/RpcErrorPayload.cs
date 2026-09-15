using MessagePack;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>Internal wire payload for a failed request.</summary>
[MessagePackObject(AllowPrivate = true)]
internal sealed partial class RpcErrorPayload
{
    /// <summary>Stable machine-readable error category.</summary>
    [Key(0)]
    public required string Code { get; init; }

    /// <summary>Redacted diagnostic message that is safe to expose to the caller.</summary>
    [Key(1)]
    public required string Message { get; init; }

    /// <summary>Application-owned strongly typed exception payload, when the exception was mapped.</summary>
    [Key(2)]
    public byte[]? MappedException { get; init; }
}