using MessagePack;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Indicates that an acknowledged RPC operation completed successfully without response data.
/// </summary>
[MessagePackObject]
public readonly partial struct RpcAck;