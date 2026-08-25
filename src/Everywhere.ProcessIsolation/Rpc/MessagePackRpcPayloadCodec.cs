using MessagePack;

namespace Everywhere.ProcessIsolation.Rpc;

/// <summary>
/// MessagePack payload codec used by the process-isolation transport.
/// The caller supplies the resolver options so generated formatters can be shared by both roles.
/// </summary>
public sealed class MessagePackRpcPayloadCodec(MessagePackSerializerOptions? options = null)
{
    /// <summary>
    /// Serializer options used for every payload. The default resolver is configured
    /// for untrusted input and a bounded object graph; callers can provide the shared
    /// generated-resolver options used by the application.
    /// </summary>
    public MessagePackSerializerOptions Options { get; } = options ?? MessagePackSerializerOptions.Standard.WithSecurity(
        MessagePackSecurity.UntrustedData.WithMaximumObjectGraphDepth(64));

    /// <summary>Serializes one typed request, response, notification, or stream item.</summary>
    public byte[] Serialize<T>(T value) => MessagePackSerializer.Serialize(value, Options);

    /// <summary>
    /// Deserializes one payload and rejects a null result because a null RPC value
    /// cannot satisfy the non-null protocol contract.
    /// </summary>
    public T Deserialize<T>(ReadOnlyMemory<byte> payload) =>
        MessagePackSerializer.Deserialize<T>(payload, Options) ??
        throw new RpcProtocolException($"The RPC payload for {typeof(T).FullName} was null.");
}