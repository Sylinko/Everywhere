using Everywhere.Chat.Documents;
using MessagePack;
using Microsoft.SemanticKernel;

namespace Everywhere.Core.Tests.Chat.Documents;

public sealed class FunctionResultContentSerializationTests
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
        var source = new FunctionResultContent("read_file", "file_system", "call-1", document);
        source.Metadata = new Dictionary<string, object?> { ["kind"] = "file" };

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<FunctionResultContent>(bytes);

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

    [Test]
    public void StandalonePromptNodeResultRoundTripsWithoutDocumentWrapper()
    {
        PromptNode node = new PromptTokenLimit(8,
            new PromptText("required "),
            new PromptText("optional content").WithPriority(0));
        var source = new FunctionResultContent("read_file", "file_system", "call-1", node);

        var bytes = MessagePackSerializer.Serialize(source);
        var restored = MessagePackSerializer.Deserialize<FunctionResultContent>(bytes);

        Assert.Multiple(() =>
        {
            Assert.That(restored.Result, Is.TypeOf<PromptTokenLimit>());
            Assert.That(restored.Result, Is.Not.TypeOf<PromptDocument>());
            Assert.That(restored.Result!.ToString(), Is.EqualTo(node.ToString()));
        });
    }
}
