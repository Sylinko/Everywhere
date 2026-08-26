using Everywhere.ProcessIsolation.Rpc;

namespace Everywhere.ProcessIsolation.Tests;

public class RpcGeneratedContractTests
{
    [Test]
    public void RpcAck_WhenSerialized_RoundTripsAsEmptyResponse()
    {
        var codec = new MessagePackRpcPayloadCodec();

        var payload = codec.Serialize(default(RpcAck));
        var result = codec.Deserialize<RpcAck>(payload);

        Assert.That(result, Is.EqualTo(default(RpcAck)));
    }
}
