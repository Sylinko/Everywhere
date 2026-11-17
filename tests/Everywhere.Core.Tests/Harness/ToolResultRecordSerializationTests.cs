using System.Text.Json;
using Everywhere.Harness;
using Everywhere.Prompting.Documents;
using Everywhere.Storage;
using MessagePack;
using NSubstitute;

namespace Everywhere.Core.Tests.Harness;

public sealed class ToolResultRecordSerializationTests
{
    [Test]
    public void PromptDocumentResultRoundTripsWithoutBeingFlattened()
    {
        PromptDocument document =
        [
            new PromptTokenLimit(8,
                new PromptText("required "),
                new PromptText("optional content").WithPriority(0))
        ];
        var source = new ToolResultRecord("read_file", "file_system", "call-1", document)
        {
            Metadata = new Dictionary<string, object?> { ["kind"] = "file" },
        };

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<ToolResultRecord>(bytes);

        var restoredDocument = restored.Result as PromptDocument;
        Assert.Multiple(() =>
        {
            Assert.That(restoredDocument, Is.Not.Null);
            Assert.That(restored.CallId, Is.EqualTo("call-1"));
            Assert.That(restoredDocument![0], Is.TypeOf<PromptTokenLimit>());
            Assert.That(((PromptTokenLimit)restoredDocument[0]).MaxTokens, Is.EqualTo(8));
            Assert.That(((PromptTokenLimit)restoredDocument[0])[1].Priority, Is.Zero);
            Assert.That(restored.Metadata?["kind"], Is.EqualTo("file"));
            Assert.That(restoredDocument.ToString(), Is.EqualTo(document.ToString()));
        });
    }

    [TestCase("null")]
    [TestCase("[1,true,{\"x\":0}]")]
    [TestCase("1.2300")]
    [TestCase("\"literal\"")]
    public void RoundTrip_WhenResultIsJson_PreservesJsonTypeAndSpelling(string json)
    {
        using var document = JsonDocument.Parse(json);
        var source = new ToolResultRecord("json", null, "call", document.RootElement);
        var restored = MessagePackSerializer.Deserialize<ToolResultRecord>(MessagePackSerializer.Serialize(source));
        Assert.That(restored.Result, Is.TypeOf<JsonElement>());
        Assert.That(((JsonElement)restored.Result!).GetRawText(), Is.EqualTo(json));
    }

    [Test]
    public async Task Capture_WhenStructuredObjectReturned_SerializesToDurableJsonElement()
    {
        var payload = new Dictionary<string, IReadOnlyList<string>>
        {
            ["answers"] = ["option A", "option B"]
        };
        var record = new ToolResultRecord("ask_user_question", "essential", "call-q", payload);
        var captured = await ToolResultCapture.CaptureAsync(record, Substitute.For<IBlobStorage>(), CancellationToken.None);
        var restored = MessagePackSerializer.Deserialize<ToolResultRecord>(MessagePackSerializer.Serialize(captured));

        Assert.That(restored.Result, Is.TypeOf<JsonElement>());
        var json = (JsonElement)restored.Result!;
        Assert.That(json.GetProperty("answers")[0].GetString(), Is.EqualTo("option A"));
    }

    [Test]
    public void StandalonePromptNodeResultRoundTripsWithoutDocumentWrapper()
    {
        PromptNode node = new PromptTokenLimit(8,
            new PromptText("required "),
            new PromptText("optional content").WithPriority(0));
        var source = new ToolResultRecord("read_file", "file_system", "call-1", node);

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<ToolResultRecord>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Result, Is.TypeOf<PromptTokenLimit>());
            Assert.That(restored.Result, Is.Not.TypeOf<PromptDocument>());
            Assert.That(restored.Result!.ToString(), Is.EqualTo(node.ToString()));
        });
    }
}
