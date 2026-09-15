namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// Maps selected exceptions to and from one application-owned wire contract.
/// Unmapped exceptions retain the RPC transport's generic remote-error representation.
/// </summary>
public interface IRpcExceptionMapper
{
    /// <summary>Attempts to serialize a recognized exception into the application-owned error contract.</summary>
    bool TrySerialize(Exception exception, MessagePackRpcPayloadCodec payloadCodec, out byte[] payload);

    /// <summary>Deserializes an application-owned error contract into its local exception representation.</summary>
    Exception Deserialize(ReadOnlyMemory<byte> payload, MessagePackRpcPayloadCodec payloadCodec);
}