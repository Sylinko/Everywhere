using MessagePack;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>Releases connection-scoped resources identified by an <see cref="RpcSafeHandle" />.</summary>
[RpcContract(5)]
public interface IRpcResourceReleaseRpc
{
    /// <summary>Releases one resource or records an early release for an acquisition still being dispatched.</summary>
    [RpcMethod(1)]
    ValueTask<RpcAck> ReleaseResourceAsync(RpcResourceReleaseRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Identifies one positive resource ID allocated by the requesting peer.</summary>
[MessagePackObject]
public sealed partial class RpcResourceReleaseRequest
{
    /// <summary>Gets the connection-scoped resource identifier.</summary>
    [Key(0)]
    public required long ResourceId { get; init; }
}