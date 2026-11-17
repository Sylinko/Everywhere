using MessagePack;
using Everywhere.Harness;

namespace Everywhere.Core.Tests.Chat;

public sealed class ToolCallRecordSerializationTests
{
    [Test]
    public void RoundTrip_WhenArgumentsContainNullValue_PreservesNullValue()
    {
        var source = new ToolCallRecord(
            "probe", null, null,
            new Dictionary<string, object?>
            {
                ["optional"] = null
            });

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<ToolCallRecord>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Arguments, Is.Not.Null);
            Assert.That(restored.Arguments, Does.ContainKey("optional"));
            Assert.That(restored.Arguments!["optional"], Is.Null);
        });
    }

    [Test]
    public void RoundTrip_WhenMetadataContainsNullValue_PreservesNullValue()
    {
        var source = new ToolCallRecord("probe", null, null, null)
        {
            Metadata = new Dictionary<string, object?>
            {
                ["optional"] = null
            }
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<ToolCallRecord>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Metadata, Is.Not.Null);
            Assert.That(restored.Metadata, Does.ContainKey("optional"));
            Assert.That(restored.Metadata!["optional"], Is.Null);
        });
    }
}
